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

### Confiança por palavra (campo `confianca` do JSON)

O Speaches devolve, para cada palavra, a probabilidade que o Whisper deu a ela; a lib guarda em `Palavra.Confianca`.
Medido em 2026-11-21 no audiolivro de 17 min (inglês, narração limpa, 2.348 palavras), sem gabarito ouvido
palavra por palavra — a análise é pelo contexto:

| Confiança | Palavras |
|---|---|
| < 0,1 | 9 (0,4%) |
| < 0,3 | 14 (0,6%) |
| < 0,5 | 29 (1,2%) |
| < 0,9 | 102 (4,3%) |

- **Começo de frase engana:** 16 das 181 primeiras palavras de frase ficaram abaixo de 0,5 (contra 13 de 2.167 no meio
  da frase), e quase todas estão certas ("A", "The", "Eat", "Come"). Um detector precisa ignorar ou tratar à parte a
  1ª palavra.
- **Nomes e palavras raras não disparam:** "Spiker", "Sponge", "beauteous", "overboiled", "mildewed" ficaram acima de 0,85.
- **Erro com confiança alta:** "Two horrible **arts** of yours" (provavelmente "aunts") veio com 0,972. Confiança sozinha
  não pega troca por palavra que existe e soa parecido; para isso é preciso conferir o sentido (LLM com candidatos de
  som parecido) ou ouvir de novo o trecho.
- Conclusão: confiança baixa no meio da frase é um gatilho barato e com poucos casos (~13 palavras em 17 min abaixo de
  0,5), mas a precisão como detector de erro **não foi medida** — falta um conjunto com erros conhecidos, de preferência
  da fala do próprio usuário no microfone, onde o Whisper erra mais que nesta narração.

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

## Glossário do usuário (tradução fixa de termos)

Opção `Transcritor:Traducao:Glossario` ([manual.md](manual.md#6-configuração)). Medido em 2026-11-21 no audiolivro
de 17 min com 4 termos ("Giant Peach" → "Pêssego Gigante", "Aunt Sponge" → "Tia Esponja", "Aunt Spiker" → "Tia Espeta",
"Auntie Spiker" → "Titia Espeta"), `translategemma:12b`, nas 23 frases que têm algum termo:

| Estratégia | Frases com a tradução fixa | Observação |
|---|---|---|
| Sem glossário (antes) | — | título saía "A Gigante Pera"; nomes ficavam em inglês |
| Dica nas instruções do prompt (protótipo) | 23/23 | perda das tags `<t>`: 12/23, contra 10/23 sem a dica (a lib já refaz sem contexto nesse caso) |
| Termo trocado no texto antes de traduzir (protótipo) | 21/23 | errou "Auntie Spiker" e uma vez escreveu "Dona Espeta" |
| **Na lib: dica + 2ª tentativa com o termo trocado** | **23/23** | arquivo inteiro traduzido em 10 min 54 s (transcrição do cache) |

- A troca no texto ficou só como 2ª tentativa, quando a tradução não traz o termo.

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

### Dicionário na tradução de palavra (etapa A)

O modelo de palavra (`gemma3:4b`) recebe os sentidos do dicionário como opções numeradas (formato de escolha
múltipla) e responde `{"opcao": n, "traducao": "..."}`. As opções vêm da maior expressão da frase que contém a
palavra e da palavra sozinha, e as que aparecem na tradução da frase ficam marcadas. O texto do modelo é descartado,
voltando à tradução da opção, quando copia a frase original ou fica longo demais.

Medido em 2026-11-21 com `transcritor avaliar-palavras docs/avaliacao/palavras-ambiguas.json` (32 palavras
ambíguas, 22 en → pt e 10 pt → en), RTX 3060:

| Situação | Sem dicionário | Com dicionário (regra final) | Tempo médio por palavra |
|---|---|---|---|
| Tradução da frase fornecida (ex.: do JSON, feita pelo 12b) | 30/32 (94%) — erra "gave up" e "running out" | **31/32 (97%)** — só usa o dicionário em expressões | 0,35 s → 0,36 s |
| Sem a tradução da frase (a lib traduz a frase com o 4b) | 26/32 (81%) — "pêra" para *peach*, "pena" para *feather*… | **29/32 (91%)** | 0,54 s → 0,73 s |

- Com a tradução da frase fornecida e **todas** as opções, o acerto caiu para 28/32 (88%): o modelo trocava o sentido
  que a tradução já indicava ("desist" em vez de "gave up"). Daí a regra: com tradução fornecida, só expressões.
- Erros que sobram com o dicionário: "honesto" para *fair* (injusto), "ride" para "pegar" (ônibus), "desist" /
  "relinquish" para "desisti".
- Duas respostas certas que o gabarito não previa foram acrescentadas antes da medição final, valendo para todas as
  colunas: "perceber" (*figure out*) e "too bad" ("que pena").
- Conjunto pequeno e as regras foram ajustadas nele: os números indicam a direção, não a precisão. Ampliar o
  conjunto antes de outras mudanças.

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
