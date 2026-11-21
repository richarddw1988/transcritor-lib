# Especificação: modelos de IA usados e testados

Última atualização: 2026-11-21. **Mantenha este arquivo em dia** sempre que um modelo, uma imagem, um padrão de
configuração ou uma medição mudar (ver [CLAUDE.md](../CLAUDE.md)).

Todos os modelos rodam **localmente**, em contêineres Docker, na GPU. Nenhum dado sai da máquina.

## Modelos em uso (padrões)

| Função | Modelo | Serviço | Onde configurar | Tamanho | Desempenho medido (RTX 3060 12 GB) |
|---|---|---|---|---|---|
| Transcrição de arquivo (tempo por palavra) | `Systran/faster-whisper-large-v3` | Speaches | `Transcritor:Arquivo:ModeloTranscricao` | ~3 GB | audiolivro de 6 min (inglês) e amostras em português sem erros relevantes; tempo só da transcrição não medido isoladamente (a 1ª chamada inclui carregar o modelo) |
| Transcrição em tempo real | `large-v3-turbo` (faster-whisper, política SimulStreaming) | WhisperLiveKit | `command` do serviço `whisperlivekit` no `docker-compose.yml` | ~1,6 GB | Confirmado ~3–3,5 s depois da fala; WER 25,5% (com `--idioma pt`) numa gravação difícil de 60 s |
| Tradução das frases do arquivo | `translategemma:12b` | Ollama | `Transcritor:Traducao:ModeloFrases` | 8,1 GB | 6,7 s/frase; melhor gramática e sentido que o 4b |
| Tradução das frases no tempo real (e comando `traduzir`) | `translategemma:4b` | Ollama | `Transcritor:Traducao:ModeloFrasesTempoReal` | 3,3 GB | 0,5–2,5 s/frase com o modelo carregado |
| Tradução de palavra solta (no contexto da frase) | `gemma3:4b` | Ollama | `Transcritor:Traducao:ModeloPalavras` | 3,4 GB | ~2,5 s/palavra; resultado fica em cache |
| Análise de sentido (blocos e "o que quis dizer"), **opcional** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloAnalise` + `AnalisarSentido=true` | 8,2 GB | ~10–13 s/frase; 99% das palavras cobertas por blocos |
| Detecção de fala (VAD) | Silero VAD (embutido) | Speaches e WhisperLiveKit | — | — | ligado por padrão nos dois |

Imagens Docker: `ghcr.io/speaches-ai/speaches:latest-cuda-12.6.3` (Speaches v0.8), `ollama/ollama:latest`,
`transcritor/whisperlivekit:0.2.27` (construída do repositório oficial, tag `v0.2.27`, CUDA 12.9).

## Modelos testados e por que foram ou não escolhidos

### Transcrição

| Modelo | Uso testado | Resultado | Decisão | Detalhes |
|---|---|---|---|---|
| `faster-whisper-large-v3` | arquivo | melhor modelo aberto em português nos benchmarks; resultados limpos nos testes | **em uso** | [pesquisa-asr.md](pesquisa-asr.md) |
| `medium` | tempo real | gravação real: WER 21,6% (`pt`) / 23,5% (auto); atraso 3,1–3,2 s | foi o padrão até 2026-11-21; trocado pelo turbo por escolha do dono, mesmo um pouco melhor nessa gravação | [pesquisa-asr.md](pesquisa-asr.md) |
| `large-v3-turbo` | tempo real | gravação real: WER 25,5% (`pt`) / 29,4% (auto); atraso 3,3–3,6 s; inventou "Hello"/"Tchau" no silêncio do começo | **em uso** | [pesquisa-asr.md](pesquisa-asr.md) |
| Qwen3-ASR-1.7B, Voxtral Mini Realtime, Parakeet TDT v3, Canary-1B-v2 | só pesquisa | números de benchmark coletados; não instalados | não testados aqui | [pesquisa-asr.md](pesquisa-asr.md) |

### Tradução e análise

| Modelo | Uso testado | Resultado | Decisão | Detalhes |
|---|---|---|---|---|
| `translategemma:4b` | frases | acerta "banco" pelo contexto com o prompt de tags; erros de gramática em texto literário ("convencê-me") | **em uso no tempo real** | [etapa-2.md](etapa-2.md) |
| `translategemma:12b` | frases | corrige os erros do 4b; 5/64 frases perderam as tags (a lib refaz sem contexto) | **em uso no arquivo** | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | frases | mistura as frases vizinhas na resposta e inventa frase | não usado para frases | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | palavra solta | certo com a tradução da frase como pista ("banco" → bench/bank); erra expressões idiomáticas ("call" → "ligar") | **em uso** | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | análise de sentido | 6,4 s/frase, mas tipos sem sentido e 7/66 traduções de bloco erradas | não usado | [etapa-4.md](etapa-4.md) |
| `gemma3:12b` | análise de sentido | 72/72 blocos e traduções certos no teste; expressões com nota | **em uso (opcional)** | [etapa-4.md](etapa-4.md) |

## Requisitos de hardware e software (testado)

| Item | Testado com | Mínimo recomendado |
|---|---|---|
| Sistema | Windows 11 Pro 26300 | Windows 10/11 64 bits (captura de áudio só no Windows) |
| GPU | NVIDIA RTX 3060 12 GB | NVIDIA com 12 GB para tudo junto; 8 GB sem a análise de sentido e sem os modelos 12b |
| Driver NVIDIA | 621.24 | 576 ou mais novo (CUDA 12.9 da imagem do WhisperLiveKit) |
| Docker Desktop | engine 29.9.3, backend WSL2 (WSL 3.0.5) | backend WSL2 com GPU |
| Memória da VM do WSL | 12 GB + 4 GB de swap (`.wslconfig`) | 10 GB ou mais (com 4 GB o Ollama trava ao trocar de modelo) |
| Disco para o Docker | ~75 GB usados | 50 GB livres na 1ª preparação (pode ficar em outro disco) |
| .NET SDK | 10.0.400 | 10.0 |
| ffmpeg/ffprobe | 9.0 (gyan.dev) | qualquer versão recente no PATH |

### Uso de VRAM

Os serviços dividem a mesma GPU. Medido em 2026-11-21:
- tempo real (WhisperLiveKit turbo) + `translategemma:4b` + Speaches ocioso: ~5–9 GB;
- arquivo com análise de sentido: `translategemma:12b` e `gemma3:12b` (~8 GB cada) são usados um depois do outro;
  rode com o WhisperLiveKit parado (`docker stop whisperlivekit`) se a GPU tiver 12 GB.

O Ollama descarrega um modelo depois de 10 min sem uso (`KeepAlive`), e o Speaches depois de 5 min (`STT_MODEL_TTL`).
