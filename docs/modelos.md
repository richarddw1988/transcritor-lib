# Especificação: modelos de IA usados e testados

Última atualização: 2026-11-21. **Mantenha este arquivo em dia** sempre que um modelo, uma imagem, um padrão de
configuração ou uma medição mudar (ver [CLAUDE.md](../CLAUDE.md)).

Todos os modelos rodam **localmente**, em contêineres Docker, na GPU. Nenhum dado sai da máquina.

## Modelos em uso (padrões)

| Função | Modelo | Serviço | Onde configurar | Tamanho | Desempenho medido (RTX 3060 12 GB) |
|---|---|---|---|---|---|
| Transcrição de arquivo (tempo por palavra) | `Systran/faster-whisper-large-v3` | Speaches | `Transcritor:Arquivo:ModeloTranscricao` | ~3 GB | audiolivro de 17 min (inglês): ~2 min 45 s de transcrição + ~15 s de reparo de 6 trechos; amostras em português sem erros relevantes. Na transcrição longa às vezes repete frase ou perde a pontuação — tratado pelo reparo de trechos (ver abaixo) |
| Transcrição em tempo real | `large-v3-turbo` (faster-whisper, política SimulStreaming) | WhisperLiveKit | `command` do serviço `whisperlivekit` no `docker-compose.yml` | ~1,6 GB | Confirmado ~3–3,5 s depois da fala; WER 25,5% (com `--idioma pt`) numa gravação difícil de 60 s |
| Tradução das frases do arquivo | `translategemma:12b` | Ollama | `Transcritor:Traducao:ModeloFrases` | 8,1 GB | 6,7 s/frase; melhor gramática e sentido que o 4b |
| Tradução das frases no tempo real (e comando `traduzir`) | `translategemma:4b` | Ollama | `Transcritor:Traducao:ModeloFrasesTempoReal` | 3,3 GB | 0,5–2,5 s/frase com o modelo carregado |
| Tradução de palavra solta (no contexto da frase) | `gemma3:4b` | Ollama | `Transcritor:Traducao:ModeloPalavras` | 3,4 GB | ~2,5 s/palavra; resultado fica em cache |
| Análise de sentido (blocos e "o que quis dizer"), **opcional** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloAnalise` + `AnalisarSentido=true` | 8,2 GB | ~10–13 s/frase; 99% das palavras cobertas por blocos |
| Detecção de fala (VAD) | Silero VAD (embutido) | Speaches e WhisperLiveKit | — | — | ligado por padrão nos dois |

Imagens Docker: `ghcr.io/speaches-ai/speaches:latest-cuda-12.6.3` (Speaches v0.8), `ollama/ollama:latest`,
`transcritor/whisperlivekit:0.2.27` (construída do repositório oficial, tag `v0.2.27`, CUDA 12.9).

### Reparo de trechos na transcrição de arquivo

O Whisper, na transcrição longa, usa o texto anterior como contexto e às vezes se perde: no audiolivro "James and
the Giant Peach" (17 min) repetiu "I'm going to chop faster and faster." 4 vezes, trocou "sipping tall glasses" por
"She was wearing small glasses" e, numa execução, escreveu 22 s sem pontuação. As palavras inventadas voltam com
duração zero. A lib (`RepararTrechosSuspeitos`, ligado por padrão) transcreve de novo, isolado, cada trecho com 3+
palavras de duração zero ou 15 s+ sem pontuação, cortando o áudio em pausas e mandando as ~40 palavras anteriores
como `prompt` (sem ele o recorte sai sem pontuação), e só troca se ficar melhor.

| Medição no audiolivro de 17 min | Sem reparo | Com reparo |
|---|---|---|
| Palavras com duração zero | 82 | 5 |
| Frases repetidas por alucinação | 4 | 0 |
| Frase mais longa | 12,4 s (27,6 s numa execução sem pontuação) | 12,5 s |
| Tempo a mais | — | ~15 s (6 trechos de 9–14 s) |

A transcrição longa não é igual a cada execução (mesmo com `temperature=0`); o reparo deixa o resultado estável.

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
| `translategemma:12b` | frases | corrige os erros do 4b; 5/64 frases perderam as tags (a lib refaz sem contexto); em frase curta às vezes inventa frases a mais ("Hurry!" → "Apresse-se! Rápido! Corra! Não perca tempo!": a lib refaz sem contexto e corta o excesso); errou "Giant Peach" → "Gigante Pera" no título | **em uso no arquivo** | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | frases | mistura as frases vizinhas na resposta e inventa frase | não usado para frases | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | palavra solta | certo com a tradução da frase como pista ("banco" → bench/bank); erra expressões idiomáticas ("call" → "ligar") | **em uso** | [etapa-2.md](etapa-2.md) |
| `gemma3:4b` | análise de sentido | 6,4 s/frase, mas tipos sem sentido e 7/66 traduções de bloco erradas | não usado | [etapa-4.md](etapa-4.md) |
| `gemma3:12b` | análise de sentido | 72/72 blocos e traduções certos no teste; expressões com nota | **em uso (opcional)** | [etapa-4.md](etapa-4.md) |

## Dicionário local (Wiktionary)

Não é modelo de IA: é um SQLite gerado do Wiktionary em inglês (extração wiktextract do kaikki.org, CC BY-SA 4.0 /
GFDL), para dar à tradução os sentidos reais de cada palavra. Opcional; como gerar e usar: [manual.md](manual.md#46-dicionário-inglês--português-opcional).

| Item | Medido em 2026-11-21 (dumps de 2026-10-30) |
|---|---|
| Download | `kaikki.org-dictionary-English.jsonl.gz` 534 MB (1.499.161 verbetes) + `kaikki.org-dictionary-Portuguese.jsonl.gz` 60 MB (447.028 verbetes) |
| Importação | 27 s; gera `dados/dicionario.db` com 60 MB |
| Conteúdo | 204.093 traduções (en → pt e as mesmas invertidas, pt → en, cada uma com o sentido em inglês), 106.867 definições curtas de palavras em português, 403.880 flexões ligadas à forma base |
| Consulta | todas as 2.348 palavras do audiolivro de 17 min: média 0,32 ms, mediana 0,26 ms, p99 1,07 ms (banco aberto); a 1ª consulta, abrindo o arquivo, ~3 ms |
| Cobertura | 97% das palavras do audiolivro com verbete (medido antes do ajuste do possessivo `'s`). Sem verbete: nomes próprios, contrações (`wasn't`, `you'd`) e palavras raras (`wistfully`, `beauteous`) |

- Com esses tempos, índice por embeddings (busca semântica) não é necessário para procurar palavra ou expressão.
- Ainda **não medido**: se dar os verbetes ao modelo de tradução de palavra melhora a escolha do sentido. O
  dicionário ainda não está ligado à tradução.

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
