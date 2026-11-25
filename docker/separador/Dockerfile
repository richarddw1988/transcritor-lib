# Separador de voz da Transcritor lib: Demucs (htdemucs_ft) atrás de uma API HTTP pequena (servidor.py).
# Separa a voz da música e dos efeitos antes do Whisper; no filme de 2 h o erro caiu de 11,1% para 9,7-10,7%
# (docs/modelos.md). Versões fixas: o Demucs 4.0.1 do PyPI é a última; torch 2.5.1 é a combinação testada.
FROM python:3.12-slim

ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_NO_CACHE_DIR=1 PYTHONUNBUFFERED=1

RUN pip install torch==2.5.1 torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cu124 \
 && pip install demucs==4.0.1 soundfile==0.12.1

WORKDIR /app
COPY servidor.py .

EXPOSE 8002
# O modelo (~320 MB) é baixado na 1ª separação para /root/.cache/torch (volume no docker-compose.yml).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8002/health', timeout=4)"
CMD ["python", "servidor.py"]
