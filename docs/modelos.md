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
| Correção de palavras ouvidas errado, **opcional e experimental** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloCorrecao` + `CorrigirTranscricao=true` | 8,2 GB | ~2,5 s por frase com palavra de confiança < 0,95; ainda troca palavras certas (ver abaixo) |
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
  0,5), mas pega pouco: na fala do usuário só 2 de 17 palavras erradas ficaram abaixo de 0,5 (ver
  [Erros reais na fala do usuário](#erros-reais-na-fala-do-usuário-avaliar-fala)).

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
| `gemma3:4b` | correção da transcrição | mesmo WER que o 12b na fala do usuário (8,0% → 7,1%), mas repetiu palavra ("quite quite") | não usado | abaixo |
| `gemma3:12b` | correção da transcrição | ver [Correção de palavras ouvidas errado](#correção-de-palavras-ouvidas-errado-experimental) | **em uso (opcional, experimental)** | abaixo |
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

### Palavras de som parecido (chave de pronúncia)

Para corrigir palavra que o reconhecimento de fala ouviu errado. Cada palavra do dicionário e suas flexões ganha
uma chave de pronúncia (Double Metaphone no inglês, biblioteca Phonix; Metaphone pt-BR, biblioteca MetaphonePtBr);
`ProcurarParecidas` devolve as de chave igual ou a uma letra, ordenadas pela soma das distâncias de pronúncia e de
escrita e pela quantidade de verbetes. Medido em 2026-11-21:

| Item | Valor |
|---|---|
| Palavras com chave | 495.880 (com as flexões); importação passou de 27 s para 32 s; banco continua com ~60 MB |
| Consulta | 5,8 ms (inglês) e 11 ms (português) com as chaves em memória; a 1ª consulta carrega as chaves (~50–150 ms) |
| Posição da palavra certa em 20 pares de confusão montados à mão (12 en, 8 pt: "wright" → "right", "xuva" → "chuva"…) | 1º lugar em 8/20; até o 5º em 16/20; até o 15º em 18/20 |

- Fora dos 15: "arts" → "aunts" (chave a 1 letra, mas escrita a 2 e dezenas de palavras mais próximas, como "parts",
  "warts") e "pesego" → "pêssego". Só a pronúncia não decide: a escolha final precisa do contexto da frase (LLM) ou de
  ouvir o trecho de novo.
- Sem frequência de uso real: "mais comum" é aproximado pela quantidade de verbetes.

### Erros reais na fala do usuário (`avaliar-fala`)

20 frases ditas pelo usuário no microfone (12 en, 8 pt, 212 palavras, `docs/avaliacao/frases-para-gravar.json`),
cheias de palavras que se confundem pelo som; gravadas com `scripts/gravar-frases.ps1` (pico de 5% a 16%) e
medidas com `transcritor avaliar-fala` em 2026-11-21:

| Item | Valor |
|---|---|
| WER | 8,0% — 14 trocas, 0 faltas, 3 sobras (inglês 14 erros em 130 palavras, 10,8%; português 3 em 82, 3,7%) |
| Confiança < 0,5 como detector | pegou 2 das 17 palavras erradas ("both", "chop"); 1 alarme falso |
| Palavra certa entre as 15 parecidas (`ProcurarParecidas` da ouvida) | 8 das 14 trocas; 1º lugar em 4 ("aunt's" → "aunts", "crashed" → "crushed", "sela", "cela") |

- **A confiança quase não ajuda aqui:** os erros vêm com 0,6 a 1,0 — "concerto" → "conserto" 0,99, "sela" → "cela"
  1,00, "can't" → "can" 0,98, "cheap" → "sheet" 0,93. São palavras que existem e soam igual ou quase; o Whisper fica
  seguro. Detectar exige conferir o sentido da frase inteira (LLM), não só a confiança da palavra.
- **Homófonos são metade dos erros** (conserto/concerto, cela/sela, bare/bear, quite/quiet, crushed/crashed): a
  certa está nas parecidas e só o contexto escolhe.
- Fora das parecidas: "can't" → "can", "cheap" → "sheet", "bought" → "both", "he" → "they", "sessão" → "exceção",
  "paws" → "pawns" — som parecido demais para a chave ou palavra trocada por outra frequente.
- Primeira rodada descartada: o áudio começava já com fala (o dispositivo leva um instante para abrir) e o pico ficava
  em 1–6%; deu WER 17,5%, com 15 faltas no começo das frases. Por isso a captura agora conta o tempo a partir do aviso
  "Gravando: pode falar" e o script mostra o pico.
- Amostra pequena (uma pessoa, 20 frases, um microfone); serve para orientar, não para comparar modelos.

### Correção de palavras ouvidas errado (experimental)

`ICorretorTranscricao` (opção `CorrigirTranscricao`, desligada): o modelo recebe a frase, as vizinhas e as palavras de
som parecido do dicionário, e devolve a frase corrigida; a lib só aceita a troca de uma palavra por outra de som
parecido. Medido em 2026-11-21 com o `gemma3:12b` na fala do usuário (20 frases, `avaliar-fala --corrigir`) e no
audiolivro de 17 min (179 frases, Whisper quase sem erro, onde toda troca é dano):

| Versão | Fala do usuário (WER 8,0% sem correção) | Audiolivro | Tempo |
|---|---|---|---|
| Modelo reescreve livre, sem trava (teste à parte) | 8,0% (acertos e erros se anulam); o `gemma3:4b` piorou para 9,9% com trocas absurdas ("knight" → "cat", "mau humor" → "bom humor") | não medido | ~4 s/frase |
| Trava de pronúncia + palavra de 4+ letras + 6 parecidas por palavra | 7,1% (4 certas, 0 erradas) | não medido | 3,4 s/frase |
| Idem, 10 parecidas | 6,6% (5 certas, 0 erradas) | 27 trocas, quase todas erradas ("know" → "knew", "terrible" → "horrible", "right" → "fight") | 3,6 s/frase |
| Idem + modelo explica antes ("analise") | 10,8% (11 certas, 8 erradas) | não medido | 5,5 s/frase |
| **Atual**: + só palavra com confiança < 0,95, sem flexão ("take" → "taken"), sem apóstrofo novo | 7,5% (3 certas, 2 erradas) | 19 trocas em 2.348 palavras, quase todas erradas ("well" → "wall", "blunt" → "plant", "stale" → "still"); 57 de 179 frases foram ao modelo | 2,6 s/frase duvidosa; audiolivro 69 s |

- **Precisão baixa em texto já certo:** o modelo "corrige" palavras que só são raras ou literárias. Na fala do usuário
  o ganho é pequeno (0,5 a 1,4 ponto de WER) e, no audiolivro, o dano (~0,8 ponto) é maior. Por isso fica desligada.
- O limite de confiança corta 2/3 das frases (mais rápido), mas também corta acertos com confiança alta
  ("conserto" → "concerto" veio com 0,99) e, ao mandar parecidas só das palavras duvidosas, parece empurrar o modelo
  a trocá-las.
- Homófonos com sentido claro na frase ("sela do cavalo", "bare paws") quase nunca foram corrigidos, mesmo com a certa
  na lista de parecidas.
- Ideias não medidas: segunda chamada que compara a frase original e a corrigida e só aceita se o modelo preferir a
  corrigida; reouvir o trecho com a palavra candidata (`hotwords`/`prompt` do Whisper); modelo maior.

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
