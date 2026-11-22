# Especificação: modelos de IA usados e testados

Última atualização: 2026-11-22. **Mantenha este arquivo em dia** sempre que um modelo, uma imagem, um padrão de
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
| Correção de palavras ouvidas errado, **opcional e experimental** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloCorrecao` + `CorrigirTranscricao=true` | 8,2 GB | cada troca conferida no áudio pelo Whisper; na fala do usuário WER 8,0% → 6,6% (3 certas, 0 erradas); no audiolivro de 17 min só uma grafia trocada, ~3 min (ver abaixo) |
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
palavra, e as que aparecem na tradução da frase ficam marcadas; palavra fora de expressão vai sem opções (regra atual,
decidida no conjunto de teste mais abaixo). O texto do modelo é descartado,
voltando à tradução da opção, quando copia a frase original ou fica longo demais.

Medido em 2026-11-21 com `transcritor avaliar-palavras docs/avaliacao/palavras-ambiguas.json` (32 palavras
ambíguas, 22 en → pt e 10 pt → en), RTX 3060:

| Situação | Sem dicionário | Com dicionário (regra final) | Tempo médio por palavra |
|---|---|---|---|
| Tradução da frase fornecida (ex.: do JSON, feita pelo 12b) | 30/32 (94%) — erra "gave up" e "running out" | **31/32 (97%)** — só usa o dicionário em expressões | 0,35 s → 0,36 s |
| Sem a tradução da frase (a lib traduz a frase com o 4b) | 26/32 (81%) — "pêra" para *peach*, "pena" para *feather*… | **29/32 (91%)** com todas as opções; 28/32 só com expressões (regra atual, ver abaixo) | 0,54 s → 0,73 s |

- Com a tradução da frase fornecida e **todas** as opções, o acerto caiu para 28/32 (88%): o modelo trocava o sentido
  que a tradução já indicava ("desist" em vez de "gave up"). Daí a regra: com tradução fornecida, só expressões.
- Erros que sobram com o dicionário: "honesto" para *fair* (injusto), "ride" para "pegar" (ônibus), "desist" /
  "relinquish" para "desisti".
- Duas respostas certas que o gabarito não previa foram acrescentadas antes da medição final, valendo para todas as
  colunas: "perceber" (*figure out*) e "too bad" ("que pena").
- Conjunto pequeno e as regras foram ajustadas nele: os números indicam a direção, não a precisão.

**Conjunto de validação** (`docs/avaliacao/palavras-validacao.json`, 78 casos novos: 54 en → pt e 24 pt → en, com
substantivos e verbos ambíguos, phrasal verbs, expressões idiomáticas, gíria e palavras pouco comuns). Não foi usado
para ajustar nenhuma regra. Medido em 2026-11-22, mesma máquina; o conjunto antigo, repetido no mesmo dia, deu os
mesmos números da tabela acima.

| Situação | Sem dicionário | Com dicionário | Tempo médio por palavra |
|---|---|---|---|
| Tradução da frase fornecida | 68/78 (87%) | 67/78 (86%) | 0,24 s → 0,40 s |
| Sem a tradução da frase | 59/78 (76%) | 59/78 (76%) | 0,43 s → 0,73 s |
| **Os dois conjuntos (110 casos), com tradução** | 98/110 (89%) | 98/110 (89%) | |
| **Os dois conjuntos (110 casos), sem tradução** | 85/110 (77%) | 88/110 (80%) | |

- No conjunto novo o dicionário não melhorou o acerto: o ganho medido nos 32 casos não se repetiu.
- Com a tradução fornecida, o dicionário acertou *kicked the bucket* e errou *broke down* ("cessar"); os outros erros
  são os mesmos nas duas colunas ("ouvidinho" para *perfect pitch*, "mole" para *mole* = toupeira, "chair" para
  "cadeira" = disciplina, "rolled" para "rolou" = aconteceu, "chave" e "mangas" devolvidas sem traduzir).
- Sem a tradução, o dicionário troca um tipo de erro por outro: conserta expressões e palavras menos comuns (*kicked the
  bucket*, *piece of cake*, *under the weather*, *eventually*, *pupils*, "puxa-saco", "cola" = cheat sheet), mas erra
  palavras comuns que o modelo sozinho acertava, escolhendo o sentido errado entre as opções (*scale* "escala" na
  balança, *nail* "pregar", *row* "linha" e "discutir", *ring* "toque", *mean* "querer", *plant* "aparelhagem",
  "ligar" a TV "call"). Daí a hipótese: sem tradução, usar o dicionário só em expressões, como já era com tradução,
  testada no conjunto abaixo.
- Casos no limite que o gabarito conta como erro: "dê o seu melhor!" para *break a leg* e "apertou" (sem "os olhos")
  para *squinted*. O gabarito foi escrito antes da medição e não foi mexido depois.

**Conjunto de teste e regra atual** (`docs/avaliacao/palavras-teste.json`, 82 casos novos: 58 en → pt e 24 pt → en,
mesma mistura do de validação, gabarito escrito antes de medir). Serviu para decidir a regra "dicionário só em
expressões, com ou sem a tradução da frase". Medido em 2026-11-22, mesma máquina:

| Situação | Sem dicionário | Dicionário completo (regra anterior) | **Só expressões (regra atual)** |
|---|---|---|---|
| Teste (82), tradução da frase fornecida | 73/82 (89%) | — (já era só expressões) | **76/82 (93%)** |
| Teste (82), sem a tradução da frase | 68/82 (83%) | 61/82 (74%) | **70/82 (85%)** |
| Validação (78), sem a tradução | 59/78 (76%) | 59/78 (76%) | **60/78 (77%)** |
| Ambíguas (32), sem a tradução | 26/32 (81%) | 29/32 (91%) | 28/32 (88%) |
| **Os três (192), sem a tradução** | 153/192 (80%) | 149/192 (78%) | **158/192 (82%)** |

- Tempo médio por palavra no teste, sem a tradução: 0,41 s sem dicionário, 0,71 s com o completo, 0,45 s só com
  expressões; com a tradução: 0,24 s → 0,39 s.
- No teste, o dicionário completo errou 13 palavras comuns que o modelo sozinho acertava, todas por escolher um sentido
  raro entre as opções: *bear* "carregar", *glasses* "vidro", *mine* "mineiros", *fan* "ventoinha" e "abanar", *tie* "amarrar", *order*
  "pedir", *set off* "irritar", *chuckled* "gracejar", "assistir" "attend", "esperar" "stand by", "corrente" (de ouro)
  "fob", "batendo" (na porta) "pound". Só com expressões, todas voltaram a acertar, e as expressões continuaram certas
  (*traffic jam*, *takes after*, "água corrente").
- O que se perde em relação ao completo: palavras sozinhas em que a opção ajudava (*toast* "brinde", "cara" = guy, e
  nas ambíguas "pêra"/"pena"); e *spill the beans* e *brought up*, em que o modelo escolheu outra opção.
- Às vezes a resposta é a frase inteira traduzida ("Eles cancelaram o casamento.", "This phone is very expensive"):
  conta como certa no gabarito, mas não é o formato esperado. Não tratado.

### Dicionário na tradução de frases (testado, não adotado)

Ideia: dar ao `translategemma` os sentidos do dicionário das palavras da frase, no mesmo lugar do prompt em que
entra a dica do glossário. Medido em 2026-11-22 com script fora da lib, sem contexto de frases vizinhas,
temperatura 0, RTX 3060. Acerto = a tradução traz uma das respostas aceitas da palavra ambígua
(`docs/avaliacao/palavras-ambiguas.json`, 32 frases).

| Situação | `translategemma:12b` | `translategemma:4b` |
|---|---|---|
| Sem dica (como hoje) | 30/32 — "madeira" para *bat*, "bebendo" para *sipping* | 29/32 — "pêra" para *peach*, "saboreando" para *sipping*, "I'm so sorry" para "que pena" |
| Dica só da palavra ambígua (todos os sentidos) | 32/32 | 29/32 — conserta *peach* e *sipping*, mas erra *bat* ("raquete") e "desisti" ("decided to stop") |
| Dica de todas as palavras de conteúdo com 2+ traduções (até 6 cada) | 32/32 | não medido |

A dica só da palavra ambígua serve de teto: na prática a lib não sabe qual palavra da frase é a difícil. A versão
realista (todas as palavras) foi comparada em 40 frases do audiolivro com o 12b:

- Sem dica, rodar duas vezes já muda 4 das 40 frases (variação do próprio modelo, mesmo com temperatura 0). Com dica,
  36 mudaram; a dica tem em média 1.470 caracteres; tempo 3,2 → 3,5 s/frase.
- Melhorou: *porcupine* "ouriço" → "porco-espinho", *chopper* "ferramenta de corte" → "machadinha", *stove* "lareira" →
  "fogão", "querida" → "meu querido" (James é menino).
- Piorou: "James" virou "Tiago" em 6 frases (o dicionário traduz o nome), *breath* "hálito" → "respiração", *stale*
  "rançoso" → "abafada", "My dear old trout" saiu sem sentido.
- Escolher só as palavras raras resolveria o ruído, mas o dicionário não tem frequência: o número de sentidos não
  separa rara de comum (*toothbrush* 1, *river* 4, *gizzard* 1). Palavras raras de verdade do audiolivro (*wistfully*,
  *beauteous*, *musty*, *curvy*) nem têm verbete.
- Conclusão: não entra na lib por ora. Para retomar, faltam uma lista de frequência de palavras para escolher as
  raras, pular nomes próprios e um avaliador de qualidade da frase inteira (o acerto da palavra ambígua do 12b já é
  30/32 sem dica).

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
| + só palavra com confiança < 0,95, sem flexão ("take" → "taken"), sem apóstrofo novo | 7,5% (3 certas, 2 erradas) | 19 trocas em 2.348 palavras, quase todas erradas ("well" → "wall", "blunt" → "plant", "stale" → "still"); 57 de 179 frases foram ao modelo | 2,6 s/frase duvidosa; audiolivro 69 s |
| + 2ª chamada que compara a frase com e sem a troca (uma troca por vez, a original é o padrão) | 7,1% (2 certas, 0 erradas) | 5 trocas: "marvellous" → "marvelous" (só grafia), "sandpit" → "sandbox", "tummy" → "dummy", "stale" → "still", "Three" → "There" (no "Chapter Three") | 3,4 s/frase duvidosa; audiolivro 86 s |
| 2ª chamada + conferência no áudio | 7,1% (2 certas, 0 erradas) | 1 troca ("marvellous" → "marvelous", só grafia) | 5,2 s/frase (todas as 20, pelo pipeline); audiolivro 340 s |
| **Atual**: sem a 2ª chamada, só a conferência no áudio (frase reouvida com a versão trocada como `prompt`, palavra nova com confiança ≥ 0,2) | **6,6% (3 certas, 0 erradas)** | **1 troca** ("marvellous" → "marvelous", só grafia) | 5,3 s/frase (todas as 20, pelo pipeline); audiolivro 181 s |

- **Precisão baixa em texto já certo:** o modelo "corrige" palavras que só são raras ou literárias. A 2ª chamada
  cortou o dano no audiolivro de 19 para 5 trocas (~0,2 ponto de WER) sem perder ganho na fala do usuário (−0,9 ponto),
  mas a correção ainda acerta pouco: 2 das 17 palavras erradas. Por isso continua desligada e experimental.
- A 2ª chamada também recusou "aunt's" → "aunts", que estava certa: o modelo prefere manter a frase ouvida.
- O limite de confiança corta 2/3 das frases (mais rápido), mas também corta acertos com confiança alta
  ("conserto" → "concerto" veio com 0,99) e, ao mandar parecidas só das palavras duvidosas, parece empurrar o modelo
  a trocá-las.
- Homófonos com sentido claro na frase ("sela do cavalo", "bare paws") quase nunca foram corrigidos, mesmo com a certa
  na lista de parecidas.
- **Conferência no áudio** (medida em 2026-11-22, `faster-whisper-large-v3`): só a palavra candidata como `hotwords`
  ou `prompt` não mudou nada em 13 trocas da fala do usuário. Já a frase inteira trocada como `prompt` faz o Whisper
  copiar a dica, mas a confiança separa: com a frase certa, a palavra nova veio com 0,25–0,77 em 8 de 10 casos
  ("invited" 0,27, "sessão" 0,77, "sela" 0,53), e 0,01–0,11 em "cheap", "bare" e "paws"; com frases erradas de
  controle, 0,00–0,09 ("woke", "bass", "mal", "peixes", "pitch"), com uma exceção ("alargada" por "alagada", 0,67).
  No audiolivro, 16 trocas erradas (as 4 que passavam pela 2ª chamada e 12 de controle, como "gizzard" → "lizard")
  vieram com confiança < 0,001 ou nem apareceram. Daí o limite de 0,2.
- Com a mesma proposta do modelo (limite de confiança 0,95, sem 2ª chamada), antes eram 3 trocas certas e 2 erradas
  na fala; a conferência no áudio recusou as 2 erradas e manteve as 3 certas. Agora o que limita é a proposta: só 3 das
  17 palavras erradas são corrigidas. Homófonos com sentido claro ("sela do cavalo", "bare paws", confiança 0,64–1,00)
  continuam sem correção.
- O tempo a mais do audiolivro (86 s → 181 s) vem de reouvir cada frase com troca proposta, com o Whisper e o
  `gemma3:12b` dividindo a GPU de 12 GB; não medido em separado.
- Ideia não medida: modelo maior para propor as trocas.

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
