"""Separador de voz (Demucs) para a Transcritor lib.

API (porta 8002):
  GET  /health      -> 200 "ok"
  POST /separar     -> corpo: WAV PCM 16 bits (mono ou estéreo, qualquer taxa; a lib manda 44,1 kHz mono em partes de
                       10 min). Resposta: WAV PCM 16 bits, 16 kHz, mono, só com a voz.
  POST /descarregar -> tira o modelo da GPU (a lib chama antes do Whisper entrar).

O modelo é carregado na primeira separação e fica na GPU até /descarregar. Uma separação por vez.
Medido no filme de 2 h (canal central): erro da transcrição 11,1% -> 9,7-10,1% (docs/modelos.md).
"""

import gc
import io
import os
import random
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import soundfile
import torch
import torchaudio.functional as F
from demucs.apply import apply_model
from demucs.pretrained import get_model

MODELO = os.environ.get("SEPARADOR_MODELO", "htdemucs_ft")
PORTA = int(os.environ.get("SEPARADOR_PORTA", "8002"))
TAXA_SAIDA = 16000

_trava = threading.Lock()
_modelo = None


def _carregar():
    global _modelo
    if _modelo is None:
        _modelo = get_model(MODELO)
        _modelo.to("cuda" if torch.cuda.is_available() else "cpu").eval()
    return _modelo


def _descarregar():
    global _modelo
    _modelo = None
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def separar(wav_bytes: bytes) -> bytes:
    dados, taxa = soundfile.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=True)
    audio = torch.from_numpy(dados.T.copy())  # (canais, amostras)
    if audio.shape[0] == 1:
        audio = audio.repeat(2, 1)  # o Demucs quebra com mono
    elif audio.shape[0] > 2:
        audio = audio[:2]

    with _trava, torch.no_grad():
        # O Demucs desloca o áudio ao acaso (shifts=1): sem semente fixa, duas separações do mesmo trecho diferem
        # (~18 dB de SNR entre elas) e a transcrição varia (9,7% e 10,1% no filme). Mesma entrada, mesma saída.
        random.seed(0)
        torch.manual_seed(0)
        modelo = _carregar()
        if taxa != modelo.samplerate:
            audio = F.resample(audio, taxa, modelo.samplerate)
        dispositivo = next(modelo.parameters()).device
        ref = audio.mean(0)
        media, desvio = ref.mean(), ref.std() + 1e-8
        entrada = ((audio - media) / desvio)[None].to(dispositivo)
        fontes = apply_model(modelo, entrada, split=True, overlap=0.25, shifts=1, progress=False)[0]
        voz = fontes[modelo.sources.index("vocals")].cpu() * desvio + media
        del entrada, fontes

    mono = voz.mean(0, keepdim=True)
    mono = F.resample(mono, modelo.samplerate, TAXA_SAIDA).clamp(-1, 1)
    saida = io.BytesIO()
    soundfile.write(saida, mono[0].numpy(), TAXA_SAIDA, subtype="PCM_16", format="WAV")
    return saida.getvalue()


class Tratador(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _responder(self, codigo: int, corpo: bytes, tipo: str = "text/plain; charset=utf-8"):
        self.send_response(codigo)
        self.send_header("Content-Type", tipo)
        self.send_header("Content-Length", str(len(corpo)))
        self.end_headers()
        self.wfile.write(corpo)

    def do_GET(self):
        if self.path == "/health":
            self._responder(200, b"ok")
        else:
            self._responder(404, b"nao encontrado")

    def do_POST(self):
        tamanho = int(self.headers.get("Content-Length", "0"))
        corpo = self.rfile.read(tamanho) if tamanho > 0 else b""
        try:
            if self.path == "/separar":
                self._responder(200, separar(corpo), "audio/wav")
            elif self.path == "/descarregar":
                with _trava:
                    _descarregar()
                self._responder(200, b"ok")
            else:
                self._responder(404, b"nao encontrado")
        except Exception as erro:  # noqa: BLE001 - devolve o motivo à lib
            self._responder(500, f"{type(erro).__name__}: {erro}".encode("utf-8"))

    def log_message(self, formato, *args):
        print(f"{self.address_string()} {formato % args}", flush=True)


if __name__ == "__main__":
    print(f"Separador de voz ({MODELO}) na porta {PORTA}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORTA), Tratador).serve_forever()
