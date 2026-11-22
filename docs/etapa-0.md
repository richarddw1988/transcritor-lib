# Etapa 0 — verificação antes de codar

Data: 2026-11-20. Fontes: docs oficiais e **código-fonte** de cada projeto (onde a doc era omissa, li o código).

Máquina detectada: RTX 3060 12 GB · Windows 11 Pro · .NET SDK 10.0.400 · Docker 29.7 (Docker Desktop estava parado — o compose foi validado com `docker compose config`, mas ainda não subiu).

## 1. O que a documentação confirma

### Speaches (arquivo) — `http://localhost:8000`
- `POST /v1/audio/transcriptions` (multipart). Campos: `file`, `model` (obrigatório), `language`, `prompt`, `response_format`, `temperature`, `timestamp_granularities[]`, `stream`, e não-padrão: `hotwords`, `without_timestamps`.
- **Tempo por palavra: confirmado** (em `src/speaches/routers/stt.py` e `executors/whisper.py`): `response_format=verbose_json` + `timestamp_granularities[]=word` (e `segment`, repetindo o campo) → faster-whisper com `word_timestamps=True`.
- Resposta = `openai.types.audio.TranscriptionVerbose`:
  `{ text, language, duration, segments:[{id, seek, start, end, text, tokens, temperature, avg_logprob, compression_ratio, no_speech_prob}], words:[{word, start, end}] }`.
  `language` vem como código ISO (`"en"`, `"pt"`) — vem direto do faster-whisper.
- Sem `language`, o idioma é detectado automaticamente.
- VAD (Silero) é aplicado sempre no servidor (padrão `_unstable_vad_filter=True`).
- Modelo **precisa ser baixado antes**: `POST /v1/models/{model_id}` ou `PRELOAD_MODELS` na subida. `GET /v1/models` lista os instalados. `WHISPER__TTL` controla a descarga da VRAM (o nome `STT_MODEL_TTL`, de outras versões, é ignorado na v0.8).
- Saúde: `GET /health`.

### WhisperLiveKit (tempo real) — `ws://localhost:8001/asr` (v0.2.27)
- Comando `wlk` (= `wlk serve`). Flags relevantes: `--model`, `--language` (padrão `auto`), `--backend-policy simulstreaming|localagreement`, `--pcm-input`, `--target-language`, `--translation-backend nllb`.
- Com `--pcm-input`: áudio em **PCM s16le, 16 kHz, mono**, frames binários de qualquer tamanho (típico 0,5 s = 16 000 bytes). Bate com o que o `Transcritor.Captura` entrega.
- Query params do WebSocket: `language`, `target_language`, `context` (≤1000 caracteres), `mode` (`full`|`diff`), `token`.
- Ao conectar, o servidor manda `{"type":"config","useAudioWorklet":true,...}`.
- Fim do áudio: frame binário vazio → servidor drena e manda `{"type":"ready_to_stop"}`, depois fecha com 1000. Ocioso 60 s → fecha com 1001.
- Mensagem de atualização (modo `full`, **sem** campo `type`):
  `{ status, lines:[{speaker, text, start:"0:00:03", end:"0:00:05", translation?, detected_language?}], buffer_transcription, buffer_translation, remaining_time_transcription, ... }`.
  `lines` = texto **confirmado** (estado completo a cada mensagem); `buffer_transcription` = **provisório**.
- Também expõe `POST /v1/audio/transcriptions` (compatível OpenAI, `verbose_json` com `words`).
- Licença Apache-2.0.

### Ollama (tradução) — `http://localhost:11434`
- `POST /api/chat` com `model`, `messages`, `stream:false`, `format` (aceita **JSON Schema**), `options` (`temperature`, `num_ctx`), `keep_alive`, `think`. Resposta: `message.content`.
- Saúde: `GET /api/version`. Modelos instalados: `GET /api/tags`. Baixar: `POST /api/pull`.
- Existe modelo próprio de tradução: `translategemma` (4b = 3,3 GB · 12b = 8,1 GB · 27b = 17 GB), com prompt fixo "You are a professional {SRC} ({code}) to {TGT} ({code}) translator...".

## 2. Pontos em que a documentação contradiz (ou completa) o prompt/plano

1. **WhisperLiveKit não tem imagem Docker publicada.** É preciso fazer build do repositório (o compose faz isso, fixado na tag `v0.2.27`). O build com CUDA é grande e demorado.
2. **Idioma de destino "por conexão" (plano, pág. 3) só vale pela metade.** `?target_language=` existe, mas só funciona se o servidor subiu com `--target-language` e a imagem foi gerada com o extra `translation`; caso contrário é **ignorado em silêncio**. E o NLLB traduz linha a linha, **sem as frases vizinhas** — contraria a regra "tradução pelo sentido com contexto".
3. **O WhisperLiveKit não emite eventos Provisório/Confirmado.** Manda o estado completo a cada atualização. A lib vai derivar os eventos: linha nova em `lines` → `Confirmado`; mudança em `buffer_transcription` → `Provisorio`. (O `mode=diff` ajuda, mas também não tem esse conceito.)
4. **Tempo real não tem tempo por palavra.** Só início/fim por linha, como texto `"H:MM:SS"` (sem fração de segundo). Os eventos terão tempo por trecho, não por palavra.
5. **Detecção automática de idioma no tempo real "puxa para o inglês"** (aviso do próprio README). Sugiro permitir forçar o idioma no `Iniciar` (opcional; `auto` como padrão).
6. **Conflito de portas**: Speaches e WhisperLiveKit usam 8000. No compose: Speaches 8000, WLK 8001, Ollama 11434.
7. **Speaches não baixa o modelo sozinho** na primeira requisição (a doc se contradiz, mas o fluxo documentado exige `POST /v1/models/{id}`). Na Etapa 1 descobri que `PRELOAD_MODELS` só existe nas versões 0.9 RC; a imagem estável (`latest-*`, v0.8) ignora a variável. Por isso o modelo é baixado uma vez com `curl.exe -X POST http://localhost:8000/v1/models/Systran/faster-whisper-large-v3`, e a lib dá erro claro com esse comando se o modelo faltar.
8. **VAD sempre ligado no Speaches**: trechos sem fala somem — bom para nós, mas os tempos continuam absolutos (verificar na Fase 0).
9. **VRAM (12 GB)**: os três ao mesmo tempo cabem só com modelos médios. Estimativa: Speaches large-v3 ~3,5 GB + WLK medium ~2,5 GB + Ollama 4B ~4 GB ≈ 10 GB. Um modelo de tradução de 12B (~8 GB) só cabe com o tempo real desligado (os TTLs descarregam sozinhos).

## 3. docker-compose

Arquivo [`docker-compose.yml`](../docker-compose.yml). Portas presas em `127.0.0.1`. Subir:

```powershell
docker compose up -d --build
docker exec ollama ollama pull <modelo-escolhido>
```

Testes rápidos depois de subir:

```powershell
curl.exe http://localhost:8000/health
curl.exe http://localhost:8000/v1/audio/transcriptions -F "file=@teste.mp3" -F "model=Systran/faster-whisper-large-v3" -F "response_format=verbose_json" -F "timestamp_granularities[]=word" -F "timestamp_granularities[]=segment"
curl.exe http://localhost:11434/api/version
```

O WhisperLiveKit tem página de teste em `http://localhost:8001/`.

## 4. Estrutura proposta da solução

Alvo: **.NET 10** (LTS), `Nullable` e `TreatWarningsAsErrors` ligados, `Directory.Build.props` e `Directory.Packages.props` centralizados.

```
transcritor-lib/
├─ docker-compose.yml
├─ Transcritor.sln
├─ Directory.Build.props / Directory.Packages.props
├─ src/
│  ├─ Transcritor.Core/          (sem dependências)
│  │   Saida, Fonte, Idioma
│  │   Modelos: ResultadoTranscricao { Arquivo, Frases }, InfoArquivo, Frase, Palavra
│  │   Contratos:
│  │     ITranscritorArquivo   → Task<ResultadoTranscricao> ProcessarAsync(caminho, Saida, IProgress<ProgressoProcessamento>?, ct)
│  │     IMotorTranscricao     → transcreve WAV 16k mono → frases + palavras + idioma   (Speaches hoje)
│  │     ITradutor             → TraduzirFrasesAsync(frases, origem, destino, ct) com janela de vizinhas
│  │                             TraduzirPalavraAsync(palavra, frase, origem, destino, ct)
│  │     IMotorTempoReal       → IAsyncEnumerable<EventoTempoReal> TranscreverAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm, OpcoesTempoReal, ct)
│  │     IFonteAudio           → IAsyncEnumerable<ReadOnlyMemory<byte>> CapturarAsync(ct)   (PCM 16k mono)
│  │     EventoTempoReal (Provisorio | Confirmado) com Texto, Traducao?, Idioma, Inicio, Fim
│  │   Erros: ServicoIndisponivelException(nomeServico, url, causa)
│  ├─ Transcritor.Arquivo/       ffprobe/ffmpeg (IConversorMidia), SpeachesMotorTranscricao,
│  │                             agrupamento palavras→frases, cache por SHA-256 (ICacheResultado em disco)
│  ├─ Transcritor.Traducao/      OllamaTradutor (/api/chat + JSON Schema), cache de palavras, prompts
│  ├─ Transcritor.TempoReal/     WhisperLiveKitMotor (ClientWebSocket atrás de IConexaoWebSocket),
│  │                             derivação Provisório/Confirmado, tradução das linhas confirmadas
│  ├─ Transcritor.Captura/       NAudio: WasapiCapture (microfone), WasapiLoopbackCapture (sistema),
│  │                             reamostragem para 16 kHz mono s16le — [SupportedOSPlatform("windows")]
│  └─ Transcritor.Cli/           System.CommandLine: `arquivo <caminho> --saida ambos`, `traduzir`, `microfone`, `sistema`, `servicos` (checa saúde)
└─ tests/
   └─ Transcritor.Tests/         xUnit + fakes de HttpMessageHandler, WebSocket, processo ffmpeg e fonte de áudio
```

Decisões embutidas:
- HTTP via `HttpClient` tipado (fake = `HttpMessageHandler`); WebSocket atrás de interface; ffmpeg/ffprobe atrás de `IExecutorProcesso`.
- Opções por `IOptions<...>` com URLs configuráveis (`SpeachesUrl`, `WhisperLiveKitUrl`, `OllamaUrl`, caminhos do ffmpeg, pasta de cache, modelos) + extensão `AddTranscritor(...)` para DI. Sem DI também funciona (construtores públicos).
- Frases: uso os `segments` do Whisper como frases e associo `words` por intervalo de tempo.
- Cache do arquivo: chave = SHA-256 do arquivo + saída pedida + modelo. Se o JSON existe, nenhum serviço é chamado. Mudar só a `Saida` reaproveita a transcrição e chama só a tradução que falta.
- Tradução de frases em lote: janela de N frases (padrão 2 antes/2 depois) como contexto, resposta em JSON Schema só com a frase-alvo.
- Erro claro: falha de conexão/timeout vira `ServicoIndisponivelException("Speaches", "http://localhost:8000", ...)`; modelo ausente vira erro próprio dizendo o comando para baixar.

## 5. Decisões tomadas

- **Tradução no tempo real: Ollama** (não o NLLB do WhisperLiveKit). A imagem do WLK sobe sem o extra `translation`.
- **Dois modelos de tradução, cada um configurável:**
  - `translategemma:4b` → frases (arquivo e tempo real).
  - `gemma3:4b` → palavra solta no contexto da frase (o app que usa a lib pede o sentido de uma palavra).
  O Ollama descarrega o que ficar ocioso (`OLLAMA_KEEP_ALIVE=10m`).
- **Ressalva a medir na Fase 0:** o `translategemma` tem prompt fixo e não recebe as frases vizinhas como instrução. Vou passar as vizinhas no próprio texto (antes/depois da frase-alvo, com marcadores) e extrair só a frase-alvo. Se ele traduzir a mais ou de menos, troco o modelo de frase para `gemma3:4b`, que segue instruções e responde em JSON Schema. A troca é só de configuração.
