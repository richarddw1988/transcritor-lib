# Pesquisa: reconhecimento de fala local para pt-BR + inglês (nov/2026)

Feita depois de um teste real no microfone (conversa, risadas e uma cantiga) em que o tempo real errou várias
palavras ("Atirei o pau no gato" → "tirei o pau no gato"). [C] = confirmado por fonte; [I] = inferência.

## O que a pesquisa encontrou

- [C] No Open ASR Leaderboard multilíngue o **Whisper large-v3** tem o menor WER em português entre os modelos
  abertos avaliados (média 4,38), à frente de Voxtral Mini 3B (4,80), Parakeet TDT 0.6B v3 (5,95) e
  Canary-1B-v2 (6,23). Fonte: <https://arxiv.org/html/2510.06961v3>.
- [C] O README do WhisperLiveKit avisa que a detecção automática de idioma "puxa para o inglês"; forçar o idioma
  (`--idioma pt`) resolve. Fonte: <https://github.com/QuentinFuxa/WhisperLiveKit>.
- [C] O WhisperLiveKit aceita `large-v3-turbo`; o SimulStreaming com `large-v3` pede ~10 GB de VRAM
  (<https://github.com/ufal/SimulStreaming>), apertado aqui com o Ollama na mesma GPU.
- [C] Não há WER publicado de português para `large-v3-turbo`, distil ou medium; a OpenAI diz só que o turbo fica
  "parecido com o large-v2" (<https://github.com/openai/whisper/discussions/2363>).
- [C] Whisper com fine-tune em português não mostra ganho claro (ex.: BuzzASR/portuguese melhora no FLEURS e piora
  no Common Voice) e quase nenhum tem versão para faster-whisper.
- [C] Alternativas com bons números em português: **Qwen3-ASR-1.7B** (FLEURS 3,92, empata com o large-v3; backend
  `qwen3-vllm` do WhisperLiveKit, exige idioma explícito; <https://arxiv.org/html/2601.21337>) e **Voxtral Mini 4B
  Realtime** (FLEURS 5,03 com 480 ms de atraso, 3,93 com 2,4 s; pede ≥16 GB;
  <https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602>).
- [C] **Supressão de ruído antes do Whisper costuma piorar o WER** (RNNoise/ASTEROID, ICAART 2024; SAM-Audio 2026).
  VAD (Silero) ajuda e já está ligado. [I] Canto é fora do domínio de todos os modelos.

## Medido aqui (tempo real, `simular` no ritmo real, RTX 3060)

| Amostra | Modelo | WER | Atraso mediano até o Confirmado | VRAM pico (GPU toda) |
|---|---|---|---|---|
| Audiolivro em inglês, 75 s (referência: arquivo com large-v3) | medium | 2,2% | 3,5 s | 7,4 GB |
| idem | large-v3-turbo | 4,3% | 3,4 s | 5,2 GB |
| Português limpo (voz sintética), 16 s | medium | 0% | 3,4 s | 4,7 GB |
| idem | large-v3-turbo | 0% | 3,0 s | 5,4 GB |

As amostras limpas não separam os dois modelos (a diferença no inglês são ~4 palavras em ~180). O problema real
foi áudio difícil (várias vozes, risada, canto), que só um áudio gravado no próprio ambiente mede.

## Decisão

- Tempo real: `large-v3-turbo` (mesmo atraso, cabe na VRAM junto com Speaches e Ollama, família large-v3).
- Usar `--idioma pt` quando a fala for em português e `--contexto` com nomes esperados.
- Próximo passo para decidir com dados: gravar 1–5 min de áudio real
  (`transcritor captura --segundos 60 --wav teste.wav`), transcrever à mão e medir `medium`, `large-v3-turbo`
  e Qwen3-ASR-1.7B no mesmo arquivo.
