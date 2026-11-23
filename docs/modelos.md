# Especificação: modelos de IA usados e testados

Última atualização: 2026-11-23. **Mantenha este arquivo em dia** sempre que um modelo, uma imagem, um padrão de
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
| Correção de palavras ouvidas errado, **opcional e experimental** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloCorrecao` + `CorrigirTranscricao=true` | 8,2 GB | cada troca conferida pelo BERT e no áudio pelo Whisper; nas 49 frases do usuário WER 6,3% → 5,6% (4 certas, 0 erradas, com os homófonos); no audiolivro de 17 min só uma grafia trocada (ver abaixo) |
| Escolha de homófonos ("cela" ↔ "sela"), ligada mesmo sem a correção experimental | `google-bert/bert-base-cased` (inglês) e `neuralmind/bert-base-portuguese-cased` (BERTimbau base), em ONNX | ONNX Runtime na CPU, dentro da lib | `Transcritor:Traducao:PastaModelosHomofonos` + `CorrigirHomofonos` | ~0,5 GB cada | fala do usuário: "cela" → "sela", WER 6,3% → 6,2% sozinha (6,0% → 5,8% com a correção); audiolivro de 17 min: 0 trocas indevidas, ~24 s (ver "Homófonos") |
| Análise de sentido (blocos e "o que quis dizer"), **opcional** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloAnalise` + `AnalisarSentido=true` | 8,2 GB | ~10–13 s/frase; 99% das palavras cobertas por blocos |
| Detecção de fala (VAD) | Silero VAD (embutido) | Speaches e WhisperLiveKit | — | — | ligado por padrão nos dois |

Imagens Docker: `transcritor/speaches:0.8.3-descarregar` (Speaches 0.8.3, `ghcr.io/speaches-ai/speaches:0.8.3-cuda-12.6.3`,
com o `DELETE /api/ps` corrigido; ver [docker/speaches/Dockerfile](../docker/speaches/Dockerfile)), `ollama/ollama:latest`,
`transcritor/whisperlivekit:0.2.27` (construída do repositório oficial, tag `v0.2.27`, CUDA 12.9, com o arquivo da
detecção de idioma corrigido montado por cima; ver [docker/whisperlivekit](../docker/whisperlivekit/align_att_base.py)).

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

### Conferência do idioma detectado

Com sotaque brasileiro, a detecção de idioma do Whisper erra e ele devolve a fala **já traduzida** para o idioma
errado (en02: "I can figure out whether…" virou "Eu posso descobrir se o clima será bom amanhã"). O Speaches não
devolve a confiança da detecção, então a lib confere assim (`ConferirIdioma`, ligada): transcreve os primeiros 30 s
também no outro idioma (português ↔ inglês) e fica com o resultado de maior confiança média por palavra. Arquivo que
cabe na amostra não é transcrito de novo; o mais longo é transcrito inteiro com o idioma vencedor forçado.

Medido em 2026-11-22 nas 20 gravações do usuário (`dados/gravacoes`, 12 em inglês e 8 em português, 7–9 s cada),
`faster-whisper-large-v3`:

| Gravação | Detecção | Confiança média por palavra, forçado em inglês | … forçado em português |
|---|---|---|---|
| en01, en04, en05, en09–en12 | en (certo) | 0,85–0,98 | 0,71–0,91 |
| en02 | pt, texto traduzido | 0,905 | 0,636 |
| en03 | pt, texto traduzido | 0,798 | 0,680 |
| en06 | pt, texto traduzido | 0,798 | 0,797 |
| en07 | pt, texto em inglês | 0,847 | 0,845 |
| en08 | pt, texto traduzido | 0,864 | 0,580 |
| pt01–pt08 | pt (certo) | 0,73–0,86 | 0,96–0,99 |

| Regra | Idioma certo |
|---|---|
| Só a detecção do Whisper | 15/20 |
| Conferência pela confiança média por palavra (adotada) | **20/20** |
| Conferência pelo `avg_logprob` dos segmentos (testada, não adotada) | 17/20 — erra en06, en10 e en11 |

- Em en06 e en07 a diferença é de 0,001–0,002: acerta, mas por pouco. Com mais casos assim, pode errar.
- Depois da conferência, as 5 gravações saem em inglês, com os erros de reconhecimento do sotaque ("She both a sheet
  of paper…"), mas no idioma certo.
- Custo, em lote, 20 gravações com `--saida Ingles` (traduz as que saem em português), cache vazio: 121 s com a
  conferência e 96 s sem. Sem ela, 13 gravações saíram em português e foram traduzidas; com ela, 8. Não separei o tempo
  só da transcrição: o custo dela é de pelo menos 25 s, ~1,2 s por arquivo curto.
- Audiolivro de 17 min (inglês, narração limpa), só transcrição: 207 s com a conferência, 199 s sem;
  as 177 frases saíram idênticas (~8 s a mais, ~4%).
- Só pt/en: se o Whisper detectar outro idioma, fica como veio. Motor sem confiança por palavra: não confere.
- O tempo real (WhisperLiveKit, `--language auto`) não tem essa conferência. Medido em 2026-11-22 com `simular` nas
  mesmas 20 gravações (`large-v3-turbo`, `simulstreaming`): o idioma saiu certo em **20/20**, inclusive nas 5 em que o
  arquivo errava (en02, en03, en06, en07 e en08 saíram em inglês, com os erros de reconhecimento do sotaque).

**2ª rodada, 49 gravações** (2026-11-23; as 20 acima + 29 novas: frases curtas ou cheias de palavras do outro idioma,
som parecido, palavras raras e quatro falas de 20–25 s; `docs/avaliacao/frases-para-gravar.json`):

| Regra (arquivo, `large-v3`) | Idioma certo | Erros |
|---|---|---|
| Só a detecção do Whisper | 35/49 | 14 gravações em inglês detectadas como "pt" (en02, 03, 06, 07, 08, 14, 19, 21, 24, 26–30) |
| **Conferência pela confiança média por palavra (adotada)** | **48/49** | en22 |
| Confiança média × cobertura de tempo das palavras | 48/49 | en22 |
| Média do log da confiança | 46/49 | en06, en15, en22 |
| `avg_logprob` dos segmentos | 46/49 | en06, en10, en11 |
| Mediana da confiança | 45/49 | en07, en10, en12, en26 |
| Menor confiança da frase | 41/49 | 8 |

- **Erro da conferência (en22):** a detecção tinha acertado o inglês ("We live near the station, but we leave early",
  confiança média 0,837, com "live" 0,54 e "leave" 0,64). Forçado em português, o Whisper traduziu com confiança alta
  ("Nós vivemos perto da estação, mas vivemos cedo", 0,867) e a tradução ganhou. É o ponto fraco da regra: uma tradução
  fluente pode sair mais confiante que a fala certa com sotaque. Nenhuma das outras regras acertou as 49; exigir que a
  confiança e o `avg_logprob` concordem para trocar também fica em 48/49 (erra en06 em vez de en22).
- **Tempo real nas 29 novas:** errou 2. A en13 ("I need a real idea for the final project") saiu traduzida para o
  **espanhol** ("necesito un verdadero idea…") e a en19 ("I think the thing on the table is mine") para o português.
  No total, 47/49. O tempo real não tem conferência: com sotaque, use `--idioma`.
- **Tempo real só entre pt e en (2026-11-23):** o WhisperLiveKit detecta o idioma uma vez por sessão, depois de 2 s de
  fala, entre os 99 idiomas do Whisper. Um arquivo corrigido (`docker/whisperlivekit/align_att_base.py`, montado pelo
  `docker-compose.yml`) faz a escolha só entre os de `TRANSCRITOR_IDIOMAS` (`pt,en`) e deixa a espera configurável
  (`TRANSCRITOR_SEGUNDOS_DETECCAO`). Medido nas 49 gravações com `simular`:

  | Espera antes de detectar | Idioma certo | Erros | 1ª frase confirmada (mediana) |
  |---|---|---|---|
  | 2 s, entre 99 idiomas (original) | 47/49 | en13 em espanhol, en19 em português | não medido |
  | **2 s, só pt e en (adotado)** | **48/49** | en19 em português | 8,3 s |
  | 3 s, só pt e en | 48/49 | en19 | 9,1 s |
  | 4 s, só pt e en | 47/49 | en05, en07 em português | 9,5 s |

  - Esperar mais não ajudou: com 3 s, o mesmo acerto e a 1ª frase ~0,8 s depois; com 4 s, dois erros novos.
  - A en19 ("I think the thing on the table is mine") continua saindo em português; para ela, só `--idioma en`.
  - A linha "Detected language" que o arquivo grava com as probabilidades de pt e en não aparece no log do contêiner
    (o nível do log desse módulo fica acima de INFO); a correção em si funciona (a en13 saiu em inglês nas 3 rodadas).

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
| `faster-whisper-large-v3` no Speaches 0.9.0-rc.3 | arquivo | fala do usuário (20 frases, 2026-11-22): WER 7,5% (0.8: 8,0%), mas frases sem ponto final e começando com minúscula ("my aunt's life near the beach but they never swim in the sea"); não devolve a confiança das palavras. A 0.9 sempre corta o áudio por VAD e transcreve os pedaços em lote (`BatchedInferencePipeline`), sem o texto anterior como contexto; não dá para desligar | não usado: a montagem das frases depende da pontuação, e a correção da confiança | abaixo |
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
| Importação | 27 s; gera `dados/dicionario.db` com 60 MB. Com as chaves de pronúncia e as listas de frequência (2026-11-22): 37 s, 61 MB |
| Frequência | [FrequencyWords](https://github.com/hermitdave/FrequencyWords) 2018 (`en_50k.txt` e `pt_br_50k.txt`, OpenSubtitles, CC BY-SA 4.0, ~0,6 MB cada): 99.268 palavras com posição |
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

### Dicionário na tradução de frases (experimental, desligado por padrão)

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
- Conclusão da 1ª rodada: não entrou na lib. Faltavam uma lista de frequência de palavras para escolher as raras,
  pular nomes próprios e um avaliador de qualidade da frase inteira.

**2ª rodada: dica só das palavras raras** (medido em 2026-11-22, mesmo script e máquina, 12b). Lista de frequência:
[FrequencyWords](https://github.com/hermitdave/FrequencyWords) 2018, `en_50k.txt` e `pt_br_50k.txt` (contagens de
legendas do OpenSubtitles, CC BY-SA 4.0, ~0,6 MB cada). Recebem dica as palavras de conteúdo fora das 10.000 mais
frequentes (vale a melhor posição entre a palavra e a forma base) que têm verbete; palavra com maiúscula no meio da
frase é tratada como nome e pula. O limiar de 10.000 foi escolhido antes de medir.

| Medição | Sem dica | Dica só das raras |
|---|---|---|
| Ambíguas (32), acerto da palavra | 30/32 | 30/32 (2 frases com dica) |
| Validação (78) | 72/78 | 72/78 (13 com dica, 6 mudaram, todas certas nas duas) |
| Teste (82) | 80/82 | 79/82 (9 com dica; *scowled* "careta" → "cara de desgosto") |
| Audiolivro, 40 frases | — | 17 com dica (média 208 caracteres, contra 1.470 com todas as palavras), 10 mudaram |

- Tempo: +0,1 s nas frases com dica (2,7 → 2,8 s/frase nos conjuntos); nas sem dica, nada muda.
- Melhorou no audiolivro: *gizzard* "bucho" → "moela", *porcupine* "ouriço" → "porco-espinho", *spoonfuls* "colheres
  de sopa" → "colheradas", *clump* "conjunto" → "amontoado" (2 frases), *clutching* "segurando" → "segurando com força".
- Piorou: "My dear old trout" perdeu o "velhinha" ("Minha querida, você está só pele e ossos"). Neutras (troca de
  palavra sem ganho claro): *mischief*, *flabby*, *unhappiness*, *seaside*, *creaky* ("rouca" → "com um tom áspero").
- Nenhum nome traduzido ("James" ficou "James"), o problema que travou a 1ª rodada.
- Nos conjuntos de palavras quase não há palavra rara, então eles medem só o dano (1 caso no limite); o ganho aparece
  em texto literário, onde a amostra é pequena (40 frases, sem gabarito).
- Situação depois da 2ª rodada: ganho pequeno em vocabulário raro, sem dano relevante no script. Implementado na lib
  (abaixo).

**Na lib, com contexto** (opção `LimiarPalavraRara`, medida com 10000; dicionário gerado com as listas; medido em
2026-11-22). Audiolivro inteiro (179 frases), `translategemma:12b` com as 2 frases vizinhas de cada lado, como o
comando `arquivo` faz. Sem gabarito: cada frase que mudou foi lida e comparada com o original.

| Item | Sem dica | Com dica |
|---|---|---|
| Tempo das 179 frases | 622 s | 602 s (variação normal) |
| Frases com dica | — | 54 (66 palavras) |
| Frases que mudaram | — | 43; as sem dica saíram idênticas nas duas rodadas |
| Melhores (leitura) | — | ~10: *seesaw* "escorregador" → "gangorra", *gizzard* "bucho" → "moela", *porcupine* "ouriço" → "porco-espinho", *spoonfuls* "colheres de sopa" → "colheradas", *crouching* "curvado" → "agachado", *eyeballs of a lizard* "olhos de uma lagartixa" → "globos oculares de um lagarto", *bristly whiskers* "pelos finos" → "pelos pretos e grossos", *clutching* "segurando com força", *churning* "se agitando", *clump* "amontoado" (2) |
| Piores (leitura) | — | ~7, duas mudando o sentido: "an ancient peach tree that never gave any peaches" virou "uma antiga macieira que nunca dava maçãs" (a dica era de *clump*, não de *peach*); "the ocean to paddle in" virou "o oceano para ele brincar com um remo". Também: "My dear old trout" perdeu o "velha"; *bony* "esquelética" → "tinha uma estrutura óssea evidente"; *jug* "jarro" → "recipiente"; *glared* "lançou um olhar furioso" → "olhou fixamente"; *piggy eyes* "semelhantes aos de um porco" → "amendoados" |
| Neutras | — | o resto (troca de palavra ou de ordem sem ganho claro) |

- A 1ª versão contava contrações como raras ("don't", "isn't" não estão na lista como uma palavra só): 67 frases com
  dica e 48 mudadas. Corrigido (possessivo vale pela palavra, contração pula) antes da rodada da tabela.
- Advérbios em -ly comuns de sentido óbvio (*enormously*, *comfortably*, *madly*) ficam fora das 10.000 e ganham dica
  sem necessidade; não tratado.
- Com o contexto das vizinhas a dica mexe mais na frase do que no script sem contexto (10 de 40 frases mudaram lá).
- **Decisão:** fica na lib desligada por padrão (`LimiarPalavraRara = 0`), como experimental. Ganhos reais em
  vocabulário raro, mas erros de sentido em partes da frase que a dica nem cita ("macieira") pesam mais num texto
  para estudo. Para retomar: dica mais curta (só a 1ª tradução de cada palavra), limiar maior (só palavras muito
  raras) ou conferir se a tradução com dica não trocou palavras que a sem dica tinha certas.

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

**2ª rodada, 49 frases** (2026-11-23, 567 palavras; as 20 acima + 29 novas, gravadas até o Enter):

| Item | Valor |
|---|---|
| WER | 6,3% — 28 trocas, 0 faltas, 8 sobras |
| Confiança < 0,5 como detector | 6 das 36 palavras erradas; 11 alarmes falsos |
| Palavra certa entre as 15 parecidas | 10 das 28 trocas |

Os erros, por tipo:
- **Som igual ou quase, só o sentido decide** (o Whisper fica seguro, 0,6–1,0): "sela" → "cela" (1,00), "concerto" →
  "conserto" (0,99), "sessão" → "exceção", "bare" → "bear", "quite" → "quiet", "aunts" → "aunt's", "stone" →
  "stone's", "crushed" → "crashed", "leave" → "live" (no tempo real).
- **Pronúncia** (o som saiu diferente): "bought" → "both", "cheap" → "sheet", "shop" → "chop", "invited" → "invaded",
  "bad" → "head", "waded" → "wandered", "acorns" → "corns", "shoveled" → "shoved", "wheelbarrow" → "barrel", "huge" →
  "whose". Das 4 frases de palavras raras (en27–en30), 3 saíram com erro no arquivo e as 4 no tempo real ("heron" → "Aaron").
- **Palavra pequena ou flexão:** "can't" → "can", "he" → "they", "a" → "the", "rained" → "raining", "it" → "it's",
  "ao" → "no", "à" → "a".

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

- **2ª rodada, 49 frases (2026-11-23):** WER 6,3% → 6,0%; 3 trocas certas ("aunt's" → "aunts", "chop" → "shop",
  "quiet" → "quite") e 1 errada ("leave" → "live", que estava certa). Os homófonos seguros ("cela", "conserto",
  "bear", "exceção") **não chegam ao modelo**: só vai a palavra com confiança abaixo de `LimiteConfiancaCorrecao`, e o
  Whisper dá 0,75–1,00 a elas. Para pegá-las, a escolha das palavras teria de incluir as que têm homófono no
  dicionário, mesmo com confiança alta. Tempo: 25,1 s por frase, contra 5,3 s na 1ª rodada; o `avaliar-fala`
  processa cada gravação como um arquivo, e a causa provável é a carga do Whisper e do `gemma3:12b` a cada arquivo
  com o `DescarregarModelosEntreEtapas` (não conferido).
- **Homófonos (2026-11-23, `CorrigirHomofonos`):** o dicionário agora guarda os homófonos que o
  Wiktionary lista (700 pares no português, 6.502 no inglês, contando os dois sentidos; "cela" ↔ "sela", "conserto" ↔
  "concerto", "their" ↔ "there"). Tentativas, medidas nas 49 frases do usuário e no audiolivro (`gemma3:12b` na
  correção, `DescarregarModelosEntreEtapas=false`):

  | Versão | Homófonos certos na fala (cela, conserto, bear) | Trocas indevidas no audiolivro | Tempo da correção no audiolivro |
  |---|---|---|---|
  | Palavra com homófono vai à correção principal, com a lista no pedido | 0 — direto no Ollama, o modelo manteve "cela", trocou o "conserto" certo e virou os dois "bear" em "bare" | não medido | não medido |
  | Pergunta por palavra, com lacuna no lugar dela, `gemma3:4b` | não medido | **49** ("four years" → "for", "your" → "you're", "right" → "rite", "there" → "their") | 2.334 s (o Ollama alternava 4b e 12b a cada pergunta) |
  | **Atual:** lacuna com o `gemma3:12b`, só grafias da lista de frequência, troca confirmada pela 2ª opinião | **0** — "sela" foi escolhida, mas a 2ª opinião recusou | **2** ("Aunt" → "Ant", "there" → "they're") | 291 s |

  - Isoladas, as lacunas até acertam: em 10 casos (5 do audiolivro, 2 da fala), `gemma3:12b` 9/10, `gemma3:4b`
    8/10, `translategemma:12b` 9/10. Mas no audiolivro há centenas de palavras com homófono, quase todas certas;
    errar 1 em 10 vira dezenas de estragos. A 2ª opinião corta o estrago, e junto os acertos.
  - Quando a mesma palavra aparece duas vezes na frase ("a cela … a cela do cavalo"), o modelo copia a outra. Num
    conjunto de 15 casos difíceis, o `gemma3:12b` errou 3 tanto com a palavra marcada quanto com a lacuna no lugar
    dela; o `gemma3:4b` foi de 3 para 2 erros.
  - A lista do Wiktionary traz grafias de brincadeira ou de dialeto escrito ("drynx", "peepul", "purrfect"); a lib
    só usa as que estão na lista de frequência.
  - Com os modelos de conversa (gemma3), a escolha não tem como dizer quão segura está, e não houve limite que
    consertasse sem estragar. O BERT, abaixo, resolve isso: dá uma probabilidade para cada grafia.
  - **Teste com BERT (modelo de lacuna), 2026-11-23, protótipo em Python, fora da lib:** `google-bert/bert-base-cased`
    (inglês) e `neuralmind/bert-base-portuguese-cased` (BERTimbau base), ~0,4 GB cada, na CPU. Nota de cada grafia =
    pseudo-log-verossimilhança dos tokens da palavra (cada um mascarado por vez, o resto da frase à vista);
    probabilidade = softmax das notas entre as grafias; troca só se a outra grafia tiver probabilidade ≥ limite.

    | Conjunto | Resultado |
    |---|---|
    | 23 casos difíceis (mesma palavra duas vezes na frase, "night"/"knight", "their"/"there", "right"/"write") | **22/23** (gemma3: 12/15); erra "o conserto de música" (dá 0,066 a "concerto") |
    | Audiolivro: 231 palavras com homófono, todas certas | trocas indevidas por limite: 10 (0,5), 6 (0,9), 5 (0,95), 3 (0,99); com 0,99 eram todas "Aunt" → "Ant" em "Aunt Spiker" (nome); sem palavra com maiúscula no meio da frase, **0** |
    | 49 frases do usuário, limite 0,99 | **1 certa** ("cela" → "sela", 0,997), **0 erradas**; não pegou "concerto" nem "bare" (o "pawns" errado ao lado atrapalha) |

    - Abaixo de 0,99 entram trocas erradas: "mirror" → "myrrh" (0,968), "pool" → "pall" (0,975), "need" → "knead" (0,912).
    - Tempo: 231 palavras em 43 s na CPU (~0,19 s cada), sem GPU.
    - **Na lib (2026-11-23):** os mesmos modelos convertidos para ONNX (`scripts/exportar-bert.py`; diferença máxima dos
      logits para o PyTorch: 3e-5), rodando pelo ONNX Runtime 1.32 na CPU e tokenizados pelo `Microsoft.ML.Tokenizers`
      (mesmos tokens do Python; probabilidades iguais às do protótipo). Medido com a correção ligada
      (`DescarregarModelosEntreEtapas=false`):

      | Conjunto | Sem homófonos | Com o BERT (limite 0,99) |
      |---|---|---|
      | 49 frases do usuário | WER 6,3% → 6,0% (3 certas, 1 errada) | **6,3% → 5,8%** (4 certas, 1 errada; a nova é "cela" → "sela") |
      | Audiolivro de 17 min | 1 troca ("marvellous" → "marvelous") | a mesma 1 troca; **0 de homófono** |
      | Tempo da correção no audiolivro | 99 s | 122 s |

      O `avaliar-fala` ficou 2,2 s/frase mais lento (5,4 contra 3,2 s), porque abre os serviços (e o BERT) de novo
      para cada gravação; num arquivo, o modelo carrega uma vez. Memória: não medida.
    - Por isso `CorrigirHomofonos` passou a ligada e, desde 2026-11-23, roda mesmo com a correção pelo modelo
      desligada (a correção em si continua experimental e desligada). Com as opções padrão, só os homófonos:

      | Conjunto | Sem homófonos | Com os homófonos |
      |---|---|---|
      | 49 frases do usuário (`avaliar-fala`, sem `--corrigir`) | WER 6,3% | **6,2%** ("cela" → "sela") |
      | Audiolivro de 17 min (transcrição do cache) | 0,5 s (tudo do cache) | 24,6 s, **0 trocas** |
- **BERT nas palavras parecidas (2026-11-23):** a mesma conta de probabilidade do BERT dos homófonos, aplicada às
  trocas de som parecido. Medido nas 49 frases (`avaliar-fala --corrigir`, homófonos ligados, contagem com o "antes"
  sem homófonos) e no audiolivro:

  | Variante | Trocas certas | Trocas erradas | WER | Audiolivro |
  |---|---|---|---|---|
  | `gemma3:12b` propõe, sem conferência | 4 | 1 ("leave" → "live") | 6,3% → 5,8% | 1 troca ("marvellous" → "marvelous", só grafia) |
  | BERT no lugar do modelo: compara a ouvida com as parecidas do dicionário, limite 0,5 / 0,9 / 0,99 | 3 / 0 / 0 | 4 / 3 / 1 ("invaded" → "envied", "corns" → "cairns", "barrel" → "beryl") | — | não medido |
  | **`gemma3:12b` propõe e o BERT confere (`ConferirCorrecaoComBert`), limite 0,5** | **4** | **0** | **6,3% → 5,6%** | a mesma 1 troca; 101 s |
  | Idem, limite 0,7 | 3 | 0 | 6,3% → 5,8% | não medido |

  - Sozinho, o BERT puxa para palavras raras que encaixam na frase; como conferência do modelo, só barra o que não
    faz sentido. Ficou ligada, com limite 0,5. A correção em si continua desligada por padrão (`CorrigirTranscricao`).
  - As contagens das variantes "BERT no lugar do modelo" vêm de uma rodada em que o "antes" já incluía os homófonos
    (cada uma contou "cela" → "sela" como errada, sem efeito); a tabela já desconta isso.
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
  `gemma3:12b` dividindo a GPU de 12 GB; não medido em separado. Desde 2026-11-22 a correção roda em duas fases
  (todas as propostas, depois toda a conferência; ver [Um modelo por vez](#um-modelo-por-vez-no-processamento-de-arquivo-descarregarmodelosentreetapas)):
  propostas ~75–85 s + conferência ~25–30 s, com o mesmo resultado.
- Modelo maior para propor as trocas: não testado. O `gemma3:27b` (~17 GB) não cabe na GPU de 12 GB nem sozinho.

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

O Ollama descarrega um modelo depois de 10 min sem uso (`KeepAlive`), e o Speaches depois de 5 min (`WHISPER__TTL` no
`docker-compose.yml`; até 2026-11-22 o arquivo usava `STT_MODEL_TTL`, nome que a v0.8 ignora, e valia o padrão de 300 s
do mesmo jeito). No processamento de arquivo a lib não espera esses tempos (abaixo).

### Um modelo por vez no processamento de arquivo (`DescarregarModelosEntreEtapas`)

Ao fim de cada etapa a lib tira o modelo usado da GPU antes de a próxima carregar o seu, e antes de cada etapa tira
o do outro serviço (Whisper ou Ollama) que tenha sobrado de outra execução. Ollama: `keep_alive` 0, ~0,3 s. Speaches:
`DELETE /api/ps/{modelo}`, ~0,4 s. A correção passou a rodar em duas fases: primeiro o `gemma3:12b` propõe as trocas
de todas as frases, depois o Whisper confere cada uma no áudio.

O `DELETE /api/ps` da 0.8.3 original trava o serviço: o gerenciador de modelos pega a própria trava
(`threading.Lock`) duas vezes; o modelo sai da GPU, mas a resposta não volta e a transcrição seguinte fica pendurada
até `docker restart speaches`. A 0.9.0-rc.3 corrige isso, mas transcreve pior (tabela de transcrição acima). O projeto
usa a 0.8.3 com `threading.RLock` ([docker/speaches/Dockerfile](../docker/speaches/Dockerfile)): o `DELETE` respondeu
204 em 0,4 s, liberou os 3,8 GB e as transcrições seguintes saíram iguais. A imagem marca o `/health` com o cabeçalho
`X-Transcritor-Descarregar`; sem ele (imagem original), a lib não manda o `DELETE` e só espera o tempo ocioso.

Medido em 2026-11-22 no audiolivro de 17 min (179 frases; transcrição do cache), correção com conferência no áudio +
tradução com o `translategemma:12b`, memória da GPU lida a cada 2 s (`nvidia-smi`):

| Item | Antes (frase a frase, Whisper ocioso por 300 s) | Um modelo por vez, esperando o TTL de 10 s | **Um modelo por vez, `DELETE` (atual)** |
|---|---|---|---|
| Início | Whisper carregado (da transcrição anterior) | GPU vazia | Whisper carregado (de propósito) |
| Tempo total | 784 s | 800 s | 878 s |
| Correção (propostas + conferência) | ~117 s, intercaladas | ~75 + ~30 s, mais 10 s de espera | ~85 + ~25 s; o Whisper do início saiu em ~5 s |
| Tradução (179 frases, mesmo texto) | ~625 s | ~640 s | ~710 s |
| Pico de memória da GPU | 12,0 GB de 12,3 GB | 10,4 GB | 10,7 GB |
| Média de memória da GPU | 10,6 GB | 9,2 GB | 9,4 GB |
| Durante a tradução | Whisper (3,9 GB) + `translategemma:12b` (8,1 GB) juntos | só o `translategemma:12b` | só o `translategemma:12b` |
| Resultado | 1 troca ("marvellous" → "marvelous"), 179 traduções | idêntico | idêntico |

- O tempo total não serve para comparar: a tradução, que é igual nas três (mesmo modelo, mesmas frases, mesmo
  resultado), variou de 625 a 710 s entre as rodadas. A correção levou ~2 min nas três. O ganho é a folga de
  ~1,3–1,6 GB no pico, que deixa o tempo real ou outro programa usar a placa ao mesmo tempo sem o Windows passar
  memória de vídeo para a RAM, e o Whisper sair na hora em vez de 10 s depois.
- Uma rodada com o `DELETE` antes de a lib descarregar "o outro serviço" começou com o Whisper sobrando de testes
  manuais; como a transcrição veio do cache, a lib não o tirou e o pico foi 12,0 GB (862 s). Daí a regra de descarregar
  o outro serviço antes de cada etapa.
- Uma 1ª rodada de "depois" com o nome errado da variável (`STT_MODEL_TTL`) deixou o Whisper na GPU durante a
  tradução: 817 s e pico de 12,0 GB. Foi assim que o nome errado apareceu.
**Vários arquivos: um por vez ou em lote** (`ProcessarLoteAsync`, comando `arquivo a b c`). Medido em 2026-11-22 com
10 gravações curtas da fala do usuário (7–9 s cada, `dados/gravacoes/en01..en10`), transcrição + tradução com o
`translategemma:12b`, cache vazio, GPU vazia no início; tempo pelo relógio das amostras de memória (1 s):

| Forma | Tempo | Pico de memória da GPU | Resultado |
|---|---|---|---|
| Um por vez, descarregando entre as etapas (padrão) | ~486 s (~49 s por arquivo) | 10,2 GB | igual nas três |
| Um por vez, sem descarregar (`DescarregarModelosEntreEtapas=false`) | ~133 s | 12,0 GB | igual |
| **Em lote, descarregando** | **~91 s** | **9,8 GB** | igual |

- Um por vez com o descarregar ligado paga, a cada arquivo, a carga do Whisper (~5–9 s) e a do `translategemma:12b`
  (~15–20 s): em arquivos curtos isso domina. Num arquivo longo (o audiolivro de 17 min) a mesma carga é ~2% do tempo.
- O lote carrega cada modelo uma vez e fica o mais rápido e com o menor pico. Para processar vários arquivos seguidos,
  use o lote; um por vez, prefira desligar o descarregar se a GPU tiver folga.
- Achado à parte, igual nas três formas: em 4 das 10 gravações em inglês, a detecção automática de idioma do Whisper
  disse "pt" e devolveu o texto já traduzido para o português ("Eu posso descobrir se o clima será bom amanhã").
  Tratado depois pela conferência do idioma (ver [Conferência do idioma detectado](#conferência-do-idioma-detectado)).
- WhisperLiveKit (`large-v3-turbo`), medido de novo em 2026-11-22 (GPU inteira, `nvidia-smi`): com o contêiner
  parado, 0,7 GB; 30 s depois de `docker start`, sem sessão, 2,6 GB (carrega o modelo ao subir, ~1,9 GB); depois de
  uma sessão curta (`simular`, 9 s), 2,8 GB, e ficou assim. Reiniciar o contêiner devolve só ~0,2 GB, porque ele carrega
  de novo; só parado solta tudo. A medição anterior (0,7–0,9 GB em repouso e 3,0 GB depois da sessão) não se repetiu; a
  causa da diferença não foi confirmada.
- `liberar-gpu` (Ollama + Speaches + parar o WhisperLiveKit): de 6,6 GB para 0,7 GB, com o `gemma3:4b` e o
  WhisperLiveKit carregados; o WhisperLiveKit parou em 2,6 s.
