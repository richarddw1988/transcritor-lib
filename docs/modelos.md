# Especificação: modelos de IA usados e testados

Última atualização: 2026-11-25. **Mantenha este arquivo em dia** sempre que um modelo, uma imagem, um padrão de
configuração ou uma medição mudar (ver [CLAUDE.md](../CLAUDE.md)).

Todos os modelos rodam **localmente**, em contêineres Docker, na GPU. Nenhum dado sai da máquina.

## Modelos em uso (padrões)

| Função | Modelo | Serviço | Onde configurar | Tamanho | Desempenho medido (RTX 3060 12 GB) |
|---|---|---|---|---|---|
| Transcrição de arquivo (tempo por palavra) | `Systran/faster-whisper-large-v3` | Speaches | `Transcritor:Arquivo:ModeloTranscricao` | ~3 GB | audiolivro de 17 min (inglês): ~2 min 45 s de transcrição + ~15 s de reparo de 6 trechos; amostras em português sem erros relevantes; FLEURS `dev` (fala lida): WER 4,1% em português e 5,8% em inglês; fala espontânea: CORAA 19,2% (português), AMI 20,2% (reunião, inglês), boa parte por formas faladas ("pra", "wanna") e hesitações que o Whisper limpa. Na transcrição longa às vezes repete frase ou perde a pontuação — tratado pelo reparo de trechos (ver abaixo); filme de 2 h (inglês, contra a legenda SDH): 11,1% em pedaços de 30 s com lista de nomes e só o canal central do 5.1 (15,5% misturando os canais), 55% transcrevendo o arquivo inteiro de uma vez (loops e falas puladas; ver "Áudio longo em pedaços") |
| Transcrição em tempo real | `large-v3-turbo` (faster-whisper, política SimulStreaming) | WhisperLiveKit | `command` do serviço `whisperlivekit` no `docker-compose.yml` | ~1,6 GB | Confirmado ~3–3,5 s depois da fala; WER 25,5% (com `--idioma pt`) numa gravação difícil de 60 s; fica parado e a lib liga na 1ª sessão: ~33 s do `docker start` à primeira palavra (2026-11-25) |
| Tradução das frases do arquivo | `translategemma:12b` | Ollama | `Transcritor:Traducao:ModeloFrases` | 8,1 GB | 6,7 s/frase; melhor gramática e sentido que o 4b; filme de 2 h: 878 frases em 1.507 s (~1,7 s/frase), chrF 66,3 contra a legenda oficial onde a transcrição saiu boa (ver "Tradução do filme") |
| Tradução das frases no tempo real (e comando `traduzir`) | `translategemma:4b` | Ollama | `Transcritor:Traducao:ModeloFrasesTempoReal` | 3,3 GB | 0,5–2,5 s/frase com o modelo carregado |
| Tradução de palavra solta (no contexto da frase) | `gemma3:4b` | Ollama | `Transcritor:Traducao:ModeloPalavras` | 3,4 GB | ~2,5 s/palavra; resultado fica em cache |
| Correção de palavras ouvidas errado, **opcional, desligada por padrão desde 2026-11-24** (`--corrigir` no Cli liga) | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloCorrecao` + `CorrigirTranscricao` | 8,2 GB | cada troca conferida pelo BERT e no áudio pelo Whisper; nas 49 frases do usuário WER 6,3% → 5,5% (5 certas, 0 erradas, com os homófonos e o gemma nos duvidosos); no audiolivro de 17 min só uma grafia trocada; no FLEURS, no CORAA e no AMI o WER não muda e custa de 9 a 33 s por frase (ver "4ª rodada") |
| Escolha de homófonos ("cela" ↔ "sela"), ligada mesmo sem a correção pelo modelo; os casos duvidosos para o BERT podem ir ao `gemma3:12b` (`ConfirmarHomofonoComModelo`, desligada por padrão desde 2026-11-24) | `google-bert/bert-base-cased` (inglês) e `neuralmind/bert-base-portuguese-cased` (BERTimbau base), em ONNX | ONNX Runtime na CPU, dentro da lib | `Transcritor:Traducao:PastaModelosHomofonos` + `CorrigirHomofonos` | ~0,5 GB cada em disco; ~0,65 GB de RAM por idioma carregado | fala do usuário: "cela" → "sela", WER 6,3% → 6,2% sozinha (6,0% → 5,8% com a correção); audiolivro de 17 min: 0 trocas indevidas, ~24 s (ver "Homófonos") |
| Análise de sentido (blocos e "o que quis dizer"), **opcional** | `gemma3:12b` | Ollama | `Transcritor:Traducao:ModeloAnalise` + `AnalisarSentido=true` | 8,2 GB | ~10–13 s/frase; 99% das palavras cobertas por blocos |
| Detecção de fala (VAD) | Silero VAD (embutido) | Speaches e WhisperLiveKit | — | — | ligado por padrão nos dois |
| Separação da voz (filmes: faixa 5.1/7.1) | Demucs `htdemucs_ft` | separador (imagem do projeto, `docker/separador`) | `Transcritor:Arquivo:SepararVoz` | ~320 MB; ~1,3 GB na GPU | filme de 2 h: erro 11,1% → 9,7–10,7%, nomes 86% → 88%; ~15 min a mais (1.367–1.425 s no total contra 520 s); com a 2ª passada nas lacunas, 9,5% e 91% (2.059 s) |

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

### Áudio longo em pedaços (filme de 2 h)

Opção `SegundosPorPedaco` (padrão 30). Medido em 2026-11-24 num filme de ação de 1 h 56 min com áudio duplo.
- **Referência:** a legenda inglesa SDH do próprio arquivo. Ela é quase palavra por palavra, mas encurta falas e não
  traz tudo, então o WER tem um piso que não foi medido.
- **Comparação:** trecho a trecho pelo tempo das legendas, sem as descrições de som (`[música]`, `[Jerrod]`) nem as
  letras de música, em minúsculas e sem pontuação.
- **Nomes:** 27 nomes de personagens e lugares, os que alguém tiraria da página do filme. Eles aparecem 214 vezes na
  legenda.
- RTX 3060, `large-v3`, `--idioma en`.

| Como transcreve | WER | Nomes certos | Tempo |
|---|---|---|---|
| Arquivo inteiro de uma vez (como era), sem lista de nomes | 55% | 47% | 1.026 s |
| Pedaços de 10 min, sem lista | 28,6% | 62% | 589 s |
| Pedaços de 10 min + lista | 26,6% | 71% | 568 s |
| Pedaços de 2 min + lista | 25,0% | 72% | 542 s |
| Pedaços de 2 min + lista + retranscrever buracos de 6 s+ sem palavras | 24,3% | 74% | não medido |
| Pedaços de 30 s + lista | 16,1% | 81% | 573 s |
| Pedaços de 30 s + lista + dica das últimas palavras do pedaço anterior + filtro de frases inventadas | 15,6% | 81% | 556 s |
| O mesmo + pedaço em loop refeito subindo a temperatura (2026-11-25) | 15,5% | 81% | 533 s |
| O mesmo, só com o canal central do 5.1 (2026-11-25) | 11,1% | 86% | 520 s |
| O mesmo + voz separada pelo Demucs (2026-11-25; 9,7–10,7% conforme a semente) | 10,7% | 88% | 1.367 s (separação incluída) |
| **O mesmo + 2ª passada nas lacunas** (adotado, 2026-11-25) | **9,5%** | **91%** | 2.059 s |

**O que acontecia no arquivo inteiro:**
- **Loop.** O Whisper usa o texto anterior como dica e, a partir de 1 h 9 min, escreveu "no, no, no…" por ~15 min. Depois
  repetiu "Because I have petitioned the Elder Gods for the tournament to begin." por vários minutos. Cerca de 35 min de
  diálogo sumiram. Transcrito sozinho, o mesmo trecho de 20 min não entrou em loop e teve 27,7% de WER.
- **Falas puladas.** Mesmo sem loop, falas inteiras sumiam. Uma janela de 30 s que começa com música ou luta é tratada
  como sem fala e pulada com o diálogo junto. Recortes de 40 s com essas falas, transcritos sozinhos, saíram certos.
- O `vad_filter` do Speaches 0.8.3 não mudou nada no trecho do loop (mesmas 911 palavras).

**Como a lib faz agora:**
- Corta no ponto mais silencioso (janela de 0,5 s) a até 7,5 s de cada marca de 30 s.
- Cada pedaço vai com o vocabulário e as últimas ~40 palavras do pedaço anterior como dica. Sem essa dica, alguns
  pedaços do audiolivro saíram sem pontuação nem maiúsculas.
- Tira frases que o Whisper inventa em trecho sem fala. Foram 7 "Thanks for watching!" no filme; "He was watching." fica.
- Pedaço em loop é refeito com temperatura maior (abaixo, "Loop numa repetição real").
- Áudio de até 45 s vai inteiro, então FLEURS, CORAA, AMI e as gravações do `avaliar-fala` não mudam, a não ser que
  entrem em loop (não remedido depois da nova tentativa; não houve loop visto nessas bases).

**Loop numa repetição real (2026-11-25).** Na gravação de 60 s do microfone (`teste-mic.wav`), a pessoa diz "que que
que…" e "peraí, peraí, peraí", e o Whisper com temperatura 0 estica a repetição até o fim do pedaço ("peraí" ~50 vezes).
Perde o resto: "Eu tô gravando o áudio aqui. Não tá vendo, não?". O tempo real (WhisperLiveKit) ouviu certo.
- **A dica não é a causa:** sem as palavras do pedaço anterior, o loop fica pior ("que" mais de 100 vezes). Recortes menores
  (12 a 24 s) com e sem dica também entraram em loop em 7 de 8 tentativas.
- **O reparo de trechos não pegava:** cada "peraí," tem vírgula e tempo alinhado.
- **A correção é a do Whisper original**, que a lib desligava ao mandar `temperature=0` fixo. Se a razão de compressão
  do texto do pedaço (zlib) passa de 2,4, o pedaço é transcrito de novo a 0,2, 0,4, 0,6, 0,8 e 1,0, até sair abaixo.
  A lib fica com o resultado menos repetitivo. O Speaches aceita uma temperatura por chamada e não tem penalidade de
  repetição.

| Temperatura (pedaço do loop, 3 vezes cada) | Sem loop, com a dica | Sem loop, sem a dica |
|---|---|---|
| 0 / 0,2 | 0 de 3 | 0 de 3 |
| 0,4 | 0 de 3 | 1 de 3 |
| 0,6 | 1 de 3 | 2 de 3 |
| 0,8 / 1,0 | 3 de 3 | 3 de 3 |

- **`teste-mic.wav` pela lib, duas rodadas:** o fim voltou nas duas ("…peraí cara, eu tô gravando o áudio aqui, tu tá
  vendo?"). Com temperatura o texto varia entre as rodadas ("peraí"/"peraai", "águas"/"árvores"); o cache guarda o
  primeiro resultado.
- **Filme:** 15,5% (era 15,6%), nomes iguais, 533 s. Não foi contado quantos pedaços do filme foram refeitos.

**Áudio limpo (audiolivro de 17 min, inglês), sem gabarito:**
- 21 palavras diferentes em 2.340 entre o arquivo inteiro e os pedaços, tirando pontuação, metade para cada lado.
  - Melhor nos pedaços: "Why," e "But", que faltavam.
  - Pior nos pedaços: "jug full" (era "jugful") e "35" (era "thirty-five").
- 175 frases contra 179; nenhuma frase sem pontuação.
- Tempo: 204 s contra 223 s.

**Ainda erra:** falas que não saem, como gritos, chamados da multidão ("Kitana, our queen!" três vezes) e a primeira
palavra da fala ("Kitana, you're my daughter now" saiu "are my daughter now"). Os 7 "Kitana" que faltam são todos
desse tipo; nenhum saiu com outra grafia.

**Faixa de áudio.** O arquivo tem dublagem em português (1ª faixa) e áudio em inglês (2ª). A lib sempre convertia a 1ª;
agora, com `Idioma` forçado, converte a faixa marcada com esse idioma.

**Canal central (2026-11-25).** As duas faixas do filme são 5.1, e a lib misturava os seis canais em um, com música e
efeitos junto da fala. Em filme o diálogo fica no canal central (FC). Transcrevendo só ele (`pan=mono|c0=FC` no
ffmpeg), o erro caiu de 15,5% para 11,1% e os nomes certos foram de 81% para 86%; o tempo ficou igual (520 s). Medido
duas vezes: extraindo o canal à mão e pelo `.mkv` com a opção `UsarCanalCentral` (mesmo resultado, 11,06%).
- Não achei medição publicada disso; só um relato informal de ganho na detecção de fala.
- A lib só usa o canal central quando o ffprobe diz um layout que tem esse canal (5.1, 7.1 e afins). Em estéreo o
  filtro do ffmpeg não dá erro e sai mudo, por isso não dá para tentar e ver.
- Risco não medido: filme mixado com diálogo fora do canal central (raro). Nesse caso, `UsarCanalCentral=false`.
- Só a faixa inglesa foi medida; a dublagem em português não tem legenda de referência ao pé da letra.

**Separação da voz (Demucs), 2026-11-25, adotada para filmes (`SepararVoz = Filmes`).** Pesquisa e números publicados estão em
"Ideias pesquisadas" abaixo. Teste: canal central do filme em 44,1 kHz → `htdemucs_ft` (Demucs 4.0.1, PyTorch 2.5.1
CUDA 12.4, num contêiner `python:3.12-slim`) com `--two-stems vocals` → 16 kHz mono → lib.
- O Demucs carrega o áudio inteiro: o filme de 2 h foi derrubado por falta de memória na VM do WSL. Em partes de 10 min
  funcionou. Ele também não aceita mono (erro do próprio Demucs); foi preciso mandar o canal central como estéreo.
- Tempo da separação: ~18 min para as 2 h na RTX 3060, fora a transcrição.

| Entrada da transcrição (filme de 2 h, canal central) | WER | Nomes certos | Transcrição |
|---|---|---|---|
| Canal central (padrão atual) | 11,1% | 86% | 520 s |
| **Só a voz separada** | **9,7%** | **88%** | 527 s |
| Voz separada + o resto devolvido a −17,5 dB | 10,1% | 87% | 516 s |

Pela lib (serviço `separador`, `.mkv` → JSON): **10,1%**, nomes 88%, 1.425 s no total (separação + transcrição;
520 s sem separar), pico de 6,7 GB em uso na GPU inteira (contando ~1,2 GB de outros programas).
- **9,7% contra 10,1% é variação, não defeito do serviço:** o Demucs desloca o áudio ao acaso. Duas separações do
  mesmo trecho pelo serviço diferiram tanto (SNR 17,8 dB) quanto o serviço contra a linha de comando (18,2 dB). Agora o
  serviço fixa a semente: a mesma entrada dá a mesma saída (conferido byte a byte). Com a semente fixa, o filme deu
  10,7% (1.367 s): a separação vale de 0,4 a 1,4 ponto conforme a semente.

**2ª passada nas lacunas (2026-11-25), adotada com a voz separada (`PreencherLacunas`).** Na transcrição com a voz
separada, 3,5% das palavras da legenda estavam em falas que sumiram inteiras (69 legendas, 111 s; "No!", "You yield.",
gritos e o narrador de uma luta na TV, que parece não estar no canal central). A lib procura trechos de 0,5 s ou mais
com a voz acima de −40 dB e sem palavra a menos de 0,3 s, transcreve cada um sozinho (0,5 s de folga de cada lado, com
o vocabulário) e acrescenta as palavras que caem no trecho.
- **Frases inventadas:** sem filtro entraram 135 trechos e o erro subiu (10,1% → 10,5%), quase tudo pedaços de
  "Thank you for watching". Ficam de fora os pedaços dessas frases e os trechos com chance de silêncio do Whisper
  (`no_speech_prob`) de 0,2 ou mais. Corte medido num protótipo: 0,1 → 9,2%; 0,15 → 8,9%; 0,2 → 9,0%; 0,25 → 9,2%;
  0,3 → 9,4% (escolhido no mesmo filme; não há outro filme com legenda para conferir).
- **Pela lib, mesma separação (semente fixa):** 10,7% → **9,5%**, nomes 88% → **91%**, 1.367 s → 2.059 s (476 trechos
  retranscritos, ~11 min a mais).
- Só com a voz separada: no áudio misturado a música também passa de −40 dB.
- **Só filmes por padrão:** não medido em áudio limpo (audiolivro, FLEURS, CORAA). A pesquisa aponta que redutores de
  ruído de fala pioram o Whisper em fala limpa. Estéreo e mono não passam pelo separador (`SepararVoz = Filmes`);
  `Sempre` separa tudo, sem medição.

### Ideias pesquisadas para chegar perto de 100% (2026-11-25)

Pesquisa feita por um subagente com fontes primárias; os números são os publicados (quase sempre pelo fabricante) e
não foram conferidos aqui, salvo onde dito.
- **Separar música e efeitos antes do ASR:** DnR (ESPnet, não Whisper) 17,5% → 7,0% de WER; num pipeline de TV com
  Demucs + VAD pyannote + Whisper large-v3, −34% de palavras omitidas (sem separar o ganho de cada parte). Redutores de
  ruído de fala (não separadores de música) pioraram o Whisper large-v3 (15,4% → 22,8% no LibriSpeech com ruído).
  Medido aqui: acima.
- **Modelos mais novos:** no inglês espontâneo (AMI), Cohere Transcribe 03-2026 (8,15) e Granite 4.0 1B Speech (8,44)
  contra 15,95 do Whisper large-v3, mas sem tempo por palavra (precisaria de alinhamento forçado depois). Em português
  quase não há número publicado por idioma; o Whisper large-v3 empata em fala lida (FLEURS pt 3,9). Não medido aqui.
- **Combinar sistemas (ROVER):** ~14–20% menos erro publicado com sistemas não-Whisper; 2–3× o tempo. Não medido.
- **Correção por LLM com N hipóteses:** ~30% relativo publicado, mas o Speaches não devolve N hipóteses e o LLM não
  recupera palavra que não foi ouvida. Não medido.
- **Detecção de fala para gritos e falas curtas:** o Silero perde fala atípica (sussurro: 37% das janelas no limiar
  padrão); WhisperX (VAD pyannote) subiu o recall de palavras no AMI de 52,1% para 60,3%. Não medido.

### Tradução do filme (2026-11-25)

O mesmo filme, com `--idioma en --saida Ambos`. A transcrição veio do cache (pedaços de 30 s + lista de nomes) e o
`translategemma:12b` traduziu as 878 frases em 1.507 s (~1,7 s/frase), na RTX 3060, sem outro programa usando a GPU.
- **Referência:** a legenda oficial em português do arquivo. Ela é tradução de legenda, encurtada e livre ("Thanks,
  man. I appreciate it." → "Valeu, cara. Obrigado."), então o chrF tem um teto bem abaixo de 100, que não foi medido.
- **Comparação:** chrF2 (caracteres, ordem 6) somado no corpus. As legendas em português com menos de 2 s entre si
  formam um trecho (292 trechos), e cada trecho pega as frases da lib cujo meio cai nele.

| Recorte | Trechos | chrF |
|---|---|---|
| Inglês sem traduzir contra o português (piso) | 300 primeiros | 21,6 |
| Todos os trechos (63 sem tradução porque a fala não foi transcrita) | 292 | 60,2 |
| Só onde a transcrição saiu boa (WER ≤ 10% contra a legenda inglesa) | 124 | **66,3** |

Lendo as 5 primeiras frases e 25 trechos sorteados entre os 124: quase tudo com o sentido certo; a maior parte da
diferença é estilo de legenda ("você sabe que..., certo?" × "sabe que..., né?"; a lib é mais formal e suaviza
palavrão). Erros vistos:
- **Sentido:** "Father" (pai) virou "Padre"; "Snug." (encaixou) virou "Aconchegante."; "And I will." virou "E eu farei
  o mesmo."
- **Gênero:** "essa pêssego".
- **Palavrão deixado em inglês:** `Johnny "fucking" Cage`.
- **Termos do universo do filme:** a legenda usa "Exoterra" e "Plano Terreno" e deixa "Finish him!" em inglês; a lib
  deixou "Outworld", traduziu "Reino da Terra" e "Acabem com ele!". O glossário do usuário (abaixo) fixa esses termos.
- **Erros que vieram da transcrição:** "When the sand is gone" saiu "sound" e foi traduzido "Quando o silêncio...";
  a fala "Nine times we have been challenged..." saiu em duas frases ("Nine" às 20:00, o resto 8 s depois) e a
  segunda, traduzida sozinha, virou "Em diversas ocasiões, enfrentamos desafios".

Os 63 trechos sem tradução e o chrF menor em todos os trechos (60,2) vêm das falas que a transcrição perdeu, não da
tradução.

### Vocabulário do assunto (dica ao Whisper)

Opções `Vocabulario` e `LerVocabularioAoLado` (arquivo `<mídia>.vocabulario.txt`), `--vocabulario` no Cli. Os nomes e
termos do assunto vão ao Whisper no campo `prompt` do Speaches, no formato "termo1, termo2, termo3.", até 800
caracteres. A conferência do idioma roda sem a dica, porque a detecção do idioma com a dica não foi medida. A dica vai
na transcrição final e nos trechos refeitos pelo reparo; nos trechos, ela vem antes do texto falado logo antes do trecho.

**Medição** (2026-11-24, RTX 3060, `large-v3`, idioma forçado, temperatura 0):
- Áudio: FLEURS `dev`, as mesmas gravações da 3ª rodada de `avaliar-fala`, 386 em português e 394 em inglês.
- Vídeos simulados: os textos foram embaralhados (semente 42) e agrupados de 10 em 10.
- Lista de cada grupo: os termos dos seus textos. Termo é palavra com maiúscula fora do começo da frase, sigla ou
  palavra fora do dicionário local (nem verbete, nem flexão, nem entre as 30 mil mais frequentes), com 3+ letras e sem
  dígito.
- Tamanho das listas: média de 15 termos em português (4 a 31) e 20 em inglês (9 a 36), até ~300 caracteres.
- Cada gravação foi transcrita três vezes: sem dica, com a lista como `prompt` e com a lista como `hotwords`.
- WER: minúsculas, sem pontuação, a mesma conta nas três condições. Não é comparável com os 4,1%/5,8% da 3ª rodada,
  que usou a normalização do `avaliar-fala`.

| Condição | WER pt | Termos certos pt | WER en | Termos certos en | Termo da lista escrito sem ser falado |
|---|---|---|---|---|---|
| Sem dica | 4,25% | 500/579 (86%) | 7,97% | 628/780 (81%) | 0 pt / 4 en |
| **`prompt`** (adotado) | **3,08%** | **564/579 (97%)** | **4,12%** | **739/780 (95%)** | 0 pt / 4 en |
| `hotwords` | 3,68% | 562/579 (97%) | 4,78% | 729/780 (93%) | 0 pt / 5 en |

- O `prompt` ganhou do `hotwords` nos dois idiomas; no inglês o WER quase caiu pela metade.
- Os termos "escritos sem ser falados" no inglês são quase todos de grafia: "Japanese" e "America" já aparecem sem
  dica (a referência escreve de outro jeito). Só "Canaan" (1 vez) pode ter vindo da lista.
- **Limite da medição:** cada lista continha todos os termos das frases do grupo. Na prática, a lista vem de quem
  conhece o assunto, e um termo que ficou fora dela não ganha nada.
- Não medido: fala espontânea (CORAA, AMI), listas grandes (perto de 800 caracteres), o tempo a mais por arquivo e o
  tempo real (o WhisperLiveKit já recebe `Contexto`, seção do tempo real no manual).

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

### Sinais de palavra duvidosa e pedaços colados (2026-11-24)

**Objetivo:** marcar no JSON as palavras prováveis de erro, com uma alternativa, para a pessoa conferir no player.

**Medição:**
- Áudio: FLEURS `dev`, 386 gravações em português e 394 em inglês, `large-v3`, idioma forçado.
- Cada gravação foi transcrita uma vez a temperatura 0 (a normal, com confiança por palavra) e duas vezes a 0,6
  (amostras).
- Errada = palavra da transcrição normal que não casa com a referência, depois de minúsculas e sem pontuação. Palavras
  com dígito ficaram de fora, porque a referência escreve números por extenso.
- Sinais medidos:
  - **discordam:** alguma amostra pôs outra coisa no lugar da palavra;
  - **confiança:** a probabilidade que o Whisper deu à palavra;
  - **fora do dicionário:** a mesma regra da 4ª rodada.

**1. Pedaços colados.** O Speaches marca o começo de cada palavra com um espaço. Às vezes ele devolve um pedaço sem
espaço que continua a palavra anterior: " adaptar" + "-se", " 1" + ".400", " U" + ".S.", " d" + "'água", " 8" +
"%". Foram 146 pedaços desses nas 780 gravações. O montador juntava tudo com espaço, e a frase saía "adaptar -se",
"1 .400". Juntar esses pedaços à palavra anterior (`MontadorFrases.Versao` 6) baixou as palavras erradas por essa
conta:

| | Antes | Juntando os pedaços |
|---|---|---|
| Português | 347 de 8.328 (4,2%) | 276 de 8.287 (3,3%) |
| Inglês | 447 de 8.085 (5,5%) | 375 de 8.018 (4,7%) |

A primeira análise, sem juntar os pedaços, superestimou os sinais abaixo. Parecia que eles pegavam 80% dos erros, mas
a maioria era esse defeito: as amostras de texto vêm com a palavra inteira, e a transcrição normal vinha partida.

**2. Sinais, com os pedaços já juntados:**

| Sinal | Marcadas pt | Precisão pt | Cobertura pt | Marcadas en | Precisão en | Cobertura en |
|---|---|---|---|---|---|---|
| Amostras discordam | 84 (1,0%) | 63% | 19% | 130 (1,6%) | 71% | 25% |
| Confiança < 0,5 | 57 (0,7%) | 56% | 12% | 109 (1,4%) | 47% | 14% |
| Confiança < 0,9 | 522 (6,3%) | 25% | 47% | 955 (11,9%) | 24% | 61% |
| Fora do dicionário | 219 (2,6%) | 33% | 26% | 255 (3,2%) | 36% | 25% |
| Discordam ou confiança < 0,5 | 108 (1,3%) | 53% | 21% | 203 (2,5%) | 57% | 31% |

Precisão = marcadas que estavam erradas; cobertura = erros que foram marcados.

- A alternativa das amostras era a palavra certa em só 10 das 53 erradas marcadas em português e em 29 das 92 em
  inglês.
- Trocar sozinho quando as duas amostras concordam contra a transcrição normal quase não mudou: 362 → 341 erros em
  português e 505 → 501 em inglês (WER sem tirar os números: 4,31% → 4,06% e 6,20% → 6,15%). As trocas que sobraram
  eram quase todas de grafia ("1 400" → "1400"), com casos piores ("prefecture" → "perfecture", "blog" → "block").

**Decisão:**
- Feito: a junção dos pedaços colados.
- Não adotado: as amostras. Elas custam duas transcrições a mais por arquivo para pegar 1 de cada 4 ou 5 erros, com
  alternativa certa em menos da metade.
- Para destacar dúvidas no player, use o que o JSON já traz: `confianca` < 0,5 marca ~1% das palavras e acerta metade
  das vezes, de graça.
- O ganho real até aqui veio do vocabulário do assunto (acima).
- Não medido: um segundo modelo diferente, como o `large-v3-turbo`, que não está instalado e precisaria ser baixado.

## Modelos testados e por que foram ou não escolhidos

### Transcrição

| Modelo | Uso testado | Resultado | Decisão | Detalhes |
|---|---|---|---|---|
| `faster-whisper-large-v3` | arquivo | melhor modelo aberto em português nos benchmarks; resultados limpos nos testes | **em uso** | [pesquisa-asr.md](pesquisa-asr.md) |
| `medium` | tempo real | gravação real: WER 21,6% (`pt`) / 23,5% (auto); atraso 3,1–3,2 s | foi o padrão até 2026-11-21; trocado pelo turbo por escolha do dono, mesmo um pouco melhor nessa gravação | [pesquisa-asr.md](pesquisa-asr.md) |
| `large-v3-turbo` | tempo real | gravação real: WER 25,5% (`pt`) / 29,4% (auto); atraso 3,3–3,6 s; inventou "Hello"/"Tchau" no silêncio do começo | **em uso** | [pesquisa-asr.md](pesquisa-asr.md) |
| `faster-whisper-large-v3` no Speaches 0.9.0-rc.3 | arquivo | fala do usuário (20 frases, 2026-11-22): WER 7,5% (0.8: 8,0%), mas frases sem ponto final e começando com minúscula ("my aunt's life near the beach but they never swim in the sea"); não devolve a confiança das palavras. A 0.9 sempre corta o áudio por VAD e transcreve os pedaços em lote (`BatchedInferencePipeline`), sem o texto anterior como contexto; não dá para desligar | não usado: a montagem das frases depende da pontuação, e a correção da confiança | abaixo |
| `large-v3-turbo` (`deepdml/faster-whisper-large-v3-turbo-ct2`, 1,5 GB) no Speaches | arquivo | 2026-11-23, opções padrão: 49 gravações do usuário WER **8,1%** contra 6,0% do large-v3 (37 trocas contra 26); audiolivro de 17 min transcrito em **58 s** contra 192 s (3,3× mais rápido), mas 37 palavras diferem do large-v3 e quase todas pioram: pulou uma frase inteira ("you disgusting little beast or you filthy nuisance or you miserable creature"), "murmured" → "and wermord", "Aunt" → "and"; acertou "aunts" onde o large-v3 ouviu "arts" | não adotado: nos arquivos a qualidade vale mais que a velocidade | recomendado em artigos de 2026 (Gladia, Google) pelo equilíbrio velocidade/precisão, sem números para português |
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
| `gemma3:12b` | correção da transcrição | ver [Correção de palavras ouvidas errado](#correção-de-palavras-ouvidas-errado) | **opcional** (ligada por padrão de 2026-11-23 a 2026-11-24; desligada depois da 4ª rodada) | abaixo |
| `gemma3:12b` | análise de sentido | 72/72 blocos e traduções certos no teste; expressões com nota | **em uso (opcional)** | [etapa-4.md](etapa-4.md) |
| `google-bert/bert-base-cased` | homófonos e conferência da correção (inglês) | 0 trocas indevidas no audiolivro; barrou "leave" → "live" | **em uso** | abaixo |
| `neuralmind/bert-base-portuguese-cased` (BERTimbau base) | homófonos e conferência da correção (português) | "cela" → "sela"; erra "concerto"; 6 trocas indevidas em 320 palavras de exemplos do Wiktionary | **em uso** | abaixo |
| `neuralmind/bert-large-portuguese-cased` (BERTimbau large) | homófonos (português) | também erra "concerto", perde "sessão" → "seção" no limite; 4 trocas indevidas; 2,7× mais lento | não usado | abaixo |
| `PORTULAN/albertina-100m-portuguese-ptbr-encoder` (DeBERTa) | homófonos (português) | 5/10 casos difíceis (base: 8/10); não troca "cela" → "sela" na gravação; 8 trocas indevidas | não usado | abaixo |
| `PORTULAN/albertina-900m-portuguese-ptbr-encoder` (DeBERTa) | homófonos (português) | 5/10; dá 0,027 a "sela" na gravação; 9 trocas indevidas ("porque" → "porquê"); 14× mais lento | não usado | abaixo |

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
- **Leitura diferente do roteiro (2026-11-23):** reouvindo com a frase do roteiro como dica do Whisper, três "erros"
  eram o que o usuário disse de fato: "I said I said" (en15, a repetição volta mesmo com a dica), "It's raining"
  (en31, "rained" fica com 0,05) e "fomos no sítio" (pt16, "ao" fica com 0,03). O campo `falado` do JSON guarda isso
  e o `avaliar-fala` compara com ele: **WER 5,5% → 4,6%** (com a correção; 5,4% antes dela), as mesmas 5 trocas
  certas e 0 erradas. Sem como confirmar de ouvido o que foi dito, sete gravações foram descartadas (movidas para
  `dados/gravacoes/descartadas/`): en08 ("gave her up", "they"), en12 ("the bear pawns"), en21 ("the" por "a", 0,14
  com a dica), en24 ("head", 0,85 mesmo com a dica), en30 ("wind barrel", 0,88 mesmo com a dica), en03 ("cheap" fica
  com 0,01 com o roteiro e "sheet" com 0,90 com "sheet pen" de dica: leu "sheet" de novo) e en25 ("wall" 0,10, "hall"
  0,13). **Com as 42 restantes** (499 palavras): WER 2,6% → **2,0%** com a correção, 3 trocas certas ("aunt's" →
  "aunts", "quiet" → "quite", "cela" → "sela") e 0 erradas ("bear" → "bare" era da en12 e "chop" → "shop", da en03).
  Nas 42, toda palavra do roteiro que ficou errada volta com confiança de 0,16 ou mais quando o roteiro é a dica.

**3ª rodada, FLEURS** (2026-11-23): divisão `dev` do [google/fleurs](https://huggingface.co/datasets/google/fleurs)
(CC BY 4.0; frases da Wikipédia lidas por vários falantes; ~400 MB, baixados em `dados/fleurs/`, fora do Git),
medida com `avaliar-fala --corrigir` e as opções padrão. A referência é a transcrição original do TSV.

| Item | Português (`pt_br`) | Inglês (`en_us`) |
|---|---|---|
| Frases / palavras | 386 / 8.441 | 394 / 8.214 |
| WER do large-v3 | **4,1%** (267 trocas, 27 faltas, 53 sobras) | **5,8%** (329 trocas, 96 faltas, 52 sobras) |
| Número por extenso na referência e em algarismo na saída ("dez" → "10"), contado como troca | 39 trocas (~0,5 ponto) | 35 trocas (~0,4 ponto) |
| Confiança < 0,5 como detector | 15 das 320 erradas; 31 alarmes falsos | 49 das 381 erradas; 66 alarmes falsos |
| Palavra certa entre as 15 parecidas | 66 das 267 trocas | 63 das 329 trocas |
| Correção: WER antes → depois | 4,1% → 4,1% | 5,9% → 5,8% |
| Trocas da correção que baixaram os erros | 3 ("caretenoide" → "carotenoide", "obrangente" → "abrangente", "calçam" → "caçam") | 7 ("Mayat" → "Mayotte" 3×, "pray" → "prey", "estatuaries" → "estuaries", "blanching" → "blending", "-Ferise" → "Therese") |
| Trocas de uma errada por outra errada | 4 ("derrebando" → "derrubando", era "derramando"; "avalização" → "avaliação", era "visualização"; "bajãs" → "beijos"; "janghans" → "jangans") | 3 ("genus" → "genius", era "Guinness"; "Bishkesh" → "Bishkek", a referência tem "Bishkek's"; "-pervading", a referência tem "all-pervading") |
| Trocas que estragaram palavra certa | **0** | **3** ("Manta" → "Malta", nome de cerveja; "theme" → "time"; "stealthily" → "stealthy") |
| Tempo do `avaliar-fala` | 9,7 s por frase (119 min) | 11,2 s por frase (128 min) |

- Em fala lida e limpa o large-v3 erra pouco. Os erros que sobram são nomes próprios, palavras raras e normalização
  (números, hífens, pontuação), e a correção quase não muda o WER. O ganho medido nas 49 frases do usuário (6,3% →
  5,5%) vem dos homófonos e da pronúncia de quem fala, que o FLEURS quase não tem.
- As 3 trocas que estragaram são todas em inglês; duas trocam uma palavra que existe e encaixa ("theme",
  "stealthily"), e nem o BERT nem o áudio seguraram. Viraram duas regras da conferência (ver "4ª rodada").
- WER com os números normalizados ("dez" igual a "10") antes de comparar: não medido.

**4ª rodada, fala espontânea e regras novas** (2026-11-24): `avaliar-fala --corrigir` com as opções padrão de então
(correção e gemma nos homófonos ligados), em quatro conjuntos, fora do Git em `dados/`:

- **FLEURS `dev`**, os mesmos da 3ª rodada. O inglês estava muito baixo (pico mediano −39 dBFS; os arquivos baixos
  tinham WER 6,4%, os outros 4,7%) e foi normalizado para pico de −3 dBFS. Nas mesmas 384 frases, os erros da
  transcrição (antes da correção) foram de 466 para 445 (~5,8% → ~5,6%).
- **CORAA v1.1 `dev`** ([gabrielrstan/CORAA-v1.1](https://huggingface.co/datasets/gabrielrstan/CORAA-v1.1); CC BY-NC-ND 4.0
  segundo o artigo): português falado espontâneo (entrevistas, conversas, aulas). Amostra de 400 trechos de 4 s ou
  mais, 80 por projeto, 6.336 palavras. A referência escreve como foi falado ("num", "cê", "né", "é é").
- **AMI** ([edinburghcstr/ami](https://huggingface.co/datasets/edinburghcstr/ami), `ihm`, `test`, 1º arquivo; CC BY 4.0):
  reuniões em inglês, 3 reuniões e 4 falantes. Amostra de 400 trechos normalizados para pico de −3 dBFS, 7.419
  palavras, referência em minúsculas.

Antes da rodada, duas regras novas na conferência, para os estragos da 3ª rodada: a frase reouvida tem de bater com
pelo menos 60% das outras palavras (`ConferenciaCorrecao.MinimoOutrasIguais`; o Whisper tinha inventado "I'll see you
next time" e confirmado "time") e advérbio em "-ily" conta como a mesma palavra que o adjetivo em "-y"
("stealthily" → "stealthy" deixa de ser troca). Nas 42 gravações do usuário nada mudou (3 certas, 0 erradas).

| Item | FLEURS pt | FLEURS en (384 de 394 frases) | CORAA | AMI |
|---|---|---|---|---|
| WER do large-v3 | 4,1% | ~5,6% | **19,2%** | **20,2%** |
| WER sem formas faladas, hesitação, repetição e números até 20 (conta aproximada, fora do `avaliar-fala`) | 4,9% (5,2% na mesma conta crua) | 6,6% (6,9%) | **15,3%** (19,1%) | **14,7%** (20,1%) |
| Correção: WER antes → depois | 4,1% → 4,1% | não calculado (rodada parou) | 19,2% → 19,2% | 20,1% → 20,2% |
| Trocas que acertaram | 3 | 6 ("Mayat" → "Mayotte" 2×, "pray" → "prey", "blanching" → "blending", "estatuaries" → "estuaries", "-Ferise" → "Therese") | 1 ("capu" → "capô") | 0 |
| Uma errada por outra errada, ou só grafia | 4 | 4 ("Bishkesh" → "Bishkek", "Mould" → "Mold", "healing" → "hearing", "genus" → "genius") | 5 | 6 ("summarise" → "summarize" 2×, "summarisation", "-click", "Knight" → "Night", "fluences" → "fluencies") |
| Trocas que estragaram palavra certa | 0 | **0** ("theme", "stealthily" e "Manta" não voltaram) | **2** ("poliente" → "poluente", "gayzinhos" → "gays") | **1** ("four" → "for") |
| Tempo do `avaliar-fala` com a correção | 8,7 s/frase | não calculado | **33,0 s/frase** | 25,2 s/frase |

- **Decisão (2026-11-24): `CorrigirTranscricao` e `ConfirmarHomofonoComModelo` desligadas por padrão.** Em nenhum
  conjunto a correção mudou o WER; na fala espontânea estragou mais do que acertou e triplicou o tempo. Sem as duas, o
  padrão não precisa do `gemma3:12b` (8,2 GB; `preparar-ambiente.ps1 -ComCorrecao` baixa). Os homófonos só pelo BERT
  continuam ligados. Para religar: `--corrigir` no `arquivo` do Cli ou as duas opções no `appsettings.json`.
- Duas falhas do Ollama derrubavam o arquivo inteiro: no AMI, numa frase alucinada ("Errrrr…"), o `gemma3:12b`
  respondeu 500 ("token repeat limit reached"); agora essa frase fica sem correção. No FLEURS en, duas vezes (com o
  Whisper do Speaches dividindo a GPU) o Ollama passou de 5 min sem responder e o `avaliar-fala` parou (em ~330 e em
  384 frases); causa não confirmada, continua sendo erro.
- **A confiança não aponta o erro.** A confiança mediana das palavras trocadas foi 0,93 (FLEURS pt), 0,83 (en), 0,86
  (CORAA) e 0,86 (AMI); abaixo de 0,5 ficam só 5%, 12%, 16% e 18% delas, e no CORAA e no AMI há mais palavras certas
  abaixo de 0,5 (144 e 237) do que erradas (106 e 111).
- **Palavra fora do dicionário** (nem verbete, nem flexão, nem entre as 30 mil mais frequentes) como sinal de erro:
  marca só 0,2–1,2% das palavras certas (nomes e termos do assunto: "Niigata", "kundalini", "tfidf"), mas pega 20%
  (FLEURS pt), 13% (en), 4% (CORAA) e 5% (AMI) das erradas; a maioria dos erros é uma palavra que existe.
- No CORAA e no AMI, as diferenças mais comuns não são erro de reconhecimento: "pra" → "para" (31), "tá" → "está",
  "cê" → "você", "né" e "eh" que somem; "wanna" → "want to" (34), "cause" → "because" (20), "um" e "uh" que somem.

### Correção de palavras ouvidas errado

`ICorretorTranscricao` (opção `CorrigirTranscricao`, desligada por padrão desde 2026-11-24; ver a "4ª rodada" acima e
as decisões no fim desta seção): o modelo recebe a frase, as vizinhas e as palavras de
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
    - **BERTimbau large (`neuralmind/bert-large-portuguese-cased`), 2026-11-23, mesmo protótipo:** testado para
      acertar "o conserto de música". **Não adotado:** não acerta e não melhora o resto.

      | Conjunto (português, limite 0,99) | Base (em uso) | Large |
      |---|---|---|
      | 7 casos difíceis | 6/7; erra "concerto" (0,066) | 6/7 pela grafia preferida, mas **5/7 com o limite**: erra "concerto" com mais certeza (0,035) e deixa "sessão de esportes" (prefere "seção", mas com 0,943) |
      | Frases em português das 49 gravações | "cela" → "sela" (0,997) | "cela" → "sela" (1,0); nada mais |
      | Trocas indevidas em 300 frases de exemplo do Wiktionary em português (320 palavras com homófono, todas escritas certo) | 6 ("cheque" → "xeque", "loot" → "lute", "suar" → "soar", "tença" → "tensa", "sena" → "cena") | 4 (as mesmas, menos "loot" e um dos "cheque") |
      | Tempo dessas 320 palavras na CPU | 55 s | 150 s (2,7×) |

      O large tem ~3× os parâmetros (RAM e tamanho na lib: não medidos). As trocas indevidas dos dois vêm quase todas
      de texto fora do padrão (gíria, ortografia antiga, palavra estrangeira); com o limite 0,99, ficam em 1–2% das
      palavras com homófono nesses exemplos, contra 0 no audiolivro em inglês.
    - **Albertina PT-BR (PORTULAN, DeBERTa), 2026-11-23:** modelos de lacuna mais novos, treinados em português do
      Brasil. Mesma conta, com os tokens da palavra achados pela posição no texto (servem para qualquer tokenizador) e
      a lacuna nas palavras repetidas; pesos em float32 (vêm em bfloat16, que quebra a 100M e reduz a precisão da
      900M). **Não adotados:** piores que o BERTimbau base em tudo.

      | Medida (português, limite 0,99) | BERTimbau base (em uso) | Albertina 100M (MIT) | Albertina 900M (licença "other") |
      |---|---|---|---|
      | 10 casos difíceis | **8/10** | 5/10 | 5/10 |
      | "cela" → "sela" na gravação pt07 | **troca (0,997)** | não (0,789) | não (0,027) |
      | "o conserto de música" | errado (0,047) | errado (0,166) | errado (0,004) |
      | Trocas indevidas nos 320 exemplos do Wiktionary | **6** | 8 | 9 (vários "porque" → "porquê") |
      | Tempo desses 320 | **52 s** | 111 s | 723 s |
      | Tamanho (safetensors) | 0,4 GB | 0,26 GB | 1,65 GB |
    - **Lacuna na palavra repetida (2026-11-23, em uso nos homófonos):** quando a mesma palavra aparece de novo na
      frase, o BERT tende a copiar a outra ocorrência. Na escolha de homófonos, as outras ocorrências viram `[MASK]`.
      Achado numa pesquisa por correção pelo contexto: o LanguageTool (API pública, 17 frases de teste) só pegou
      "concertar", "viajem", "piece"/"peace" e "right"/"write", e deixou passar "cela", "concerto", "sessão",
      "cheque" e "there"/"their"; a literatura usa o mesmo desenho da lib (candidatos de som parecido + modelo de
      lacuna + limite alto).

      | Medida | Sem a lacuna | Com a lacuna |
      |---|---|---|
      | "O concerto do carro … o concerto de música." (1º "concerto") | "conserto" com 0,751: não troca | **0,992: troca certo** |
      | "o conserto de música" | 0,066 para "concerto" | 0,047: continua errado |
      | Trocas indevidas nas 34 palavras com homófono repetidas na frase (exemplos do Wiktionary e audiolivro) | 0 | 0 |
      | 49 frases do usuário (`avaliar-fala`, com e sem `--corrigir`) | 6,2%; com a correção 6,3% → 5,6% (4 certas, 0 erradas) | o mesmo |
      | Audiolivro de 17 min, opções padrão | 1 troca ("marvellous" → "marvelous") | a mesma; 100,6 s |

      Na conferência das trocas do modelo de conversa (`ConferirCorrecaoComBert`) a lacuna fica desligada: ligada, perdeu
      "quiet quiet" → "quite quiet" (3 certas em vez de 4), porque ali a palavra repetida é a pista.
    - **BERT + `gemma3:12b` nos homófonos duvidosos (2026-11-23, `ConfirmarHomofonoComModelo`, ligada por
      padrão desde então):** os modelos de lacuna não sabem que se diz "concerto de música"; o modelo de conversa sabe. Quando
      o BERT dá à outra grafia entre 0,02 e 0,99, o `gemma3:12b` vê as duas frases ("1: … 2: …") e diz qual está escrita
      certo, duas vezes, com a ordem trocada; só troca se escolher a outra nas duas (sozinho, escolhia pela posição;
      com a palavra marcada entre colchetes, escolheu a opção 2 em 5 de 6). Com duas outras grafias que ganham da
      ouvida ("There house": "Their" e "They're"), fica a que ganha da outra. Medido com `avaliar-homofonos` (texto) e
      `avaliar-fala` (sem `--corrigir`), com o WhisperLiveKit parado:

      | Texto | Só o BERT (padrão) | BERT + gemma |
      |---|---|---|
      | 12 casos em português com a grafia errada | 6 certas | **10 certas, 0 erradas** ("Fomos ao conserto de música", "sela da prisão", "em cheque no tabuleiro", "porquê sempre") |
      | 8 casos em inglês com a grafia errada | 5 certas | **7 certas, 0 erradas** ("There house" → "Their", "in piece" → "peace") |
      | 49 gravações do usuário | WER 6,2% | **6,0%** ("bear pawns" → "bare pawns") |
      | Audiolivro de 17 min (texto certo) | 0 trocas, 18 s | **0 trocas**, 41 s |
      | 6.745 exemplos do Wiktionary em português | 7 trocas | 14: das 7 a mais, 3 acertam a norma ("para traz" → "trás", "o porque" → "porquê"), 2 erram ("pra trás" → "traz" em fala coloquial, "passar cheque" → "xeque") e 2 são duvidosas |
      | 1.500 exemplos do Wiktionary em inglês | 29 trocas (quase todas erradas; resolvido abaixo, em "Duas notas") | 33: as 4 a mais erram em poesia ou texto estilizado ("rode" → "rowed", "fair" → "fir", "heart" → "hart") e trocam "grey" → "gray" |

      Continuam errados nos dois: "conserto" repetido na mesma frase, "seção" → "sessão", "for years" → "four".
      Os exemplos do Wiktionary não são todos texto certo pela norma atual (ortografia antiga, fala transcrita), por
      isso as trocas a mais foram conferidas uma a uma. Na 1ª rodada, com o WhisperLiveKit no ar, o Docker Desktop
      parou de responder depois de ~10 min de chamadas ao `gemma3:12b` e foi reiniciado; causa não confirmada.
      **Decisão (2026-11-23): ligada por padrão** (revogada em 2026-11-24, ver "4ª rodada"). Conferido com todas as opções padrão e o WhisperLiveKit no ar:
      49 gravações com `avaliar-fala --corrigir`: 5 trocas certas e 0 erradas, WER 6,3% → **5,5%** (antes 4 certas,
      5,6%; a nova é "bear" → "bare"); audiolivro: a mesma única troca ("marvellous" → "marvelous"), 149,5 s de correção
      (100,6 s antes, mas com o WhisperLiveKit parado; não comparável direto). Risco aceito: em texto coloquial ou
      literário ainda troca a mais às vezes ("pra trás" → "traz"). Conferido depois (2026-11-23): esse erro só
      aparece na frase do Wiktionary com fala transcrita em grafia de dialeto e marcas "^([sic])" ("oiou", "muler");
      a mesma frase escrita normal, mesmo com "pra trás" duas vezes, e outras 6 frases com "pra trás"/"traz" saem
      certas. O Whisper não escreve essas marcas, então a pergunta ao gemma não foi mudada.
    - **Duas notas, vale a menor (2026-11-23, em uso):** quase todas as 29 trocas dos exemplos em inglês acima eram
      erradas e vinham do BERT sozinho, com ≥ 0,99: "love" → "luv", "what" → "watt", "need" → "knead", "through" →
      "thru". Causa: na nota, cada token é mascarado sozinho, então uma palavra rara de vários pedaços tem cada pedaço
      adivinhado vendo os outros ("lu" + "##v") e ganha nota alta. A correção publicada (PLL-word-l2r, Kauf e Ivanova,
      2023) mascara também os tokens seguintes da mesma palavra, mas sozinha erra para o outro lado ("you're" → "your",
      "nought" → "not"; 17 trocas nos exemplos em inglês) e perdeu 2 dos 6 casos certos em português. A lib calcula as
      duas notas no mesmo lote e usa, para cada grafia, a menor das duas probabilidades: só tem certeza quando as duas
      concordam. Medido com `avaliar-homofonos` e `avaliar-fala --corrigir`, opções padrão:

      | Texto | Antes | Duas notas |
      |---|---|---|
      | 12 casos em português com a grafia errada (só BERT / com o gemma) | 6 / 10 certas | 4 / **10** certas, 0 erradas |
      | 8 casos em inglês com a grafia errada (só BERT / com o gemma) | 5 / 7 certas | 5 / **7** certas, 0 erradas |
      | 6.745 exemplos do Wiktionary em português, com o gemma | 14 trocas, 172 s | **12**, 140 s (saíram "tença" → "tensa" e um "passar cheque" → "xeque") |
      | 1.500 exemplos do Wiktionary em inglês, com o gemma | 33 trocas, 31 min | **6**, 5,2 min; **3** depois de tirar colchetes em volta da palavra antes de olhar a maiúscula ("[Martin Luther King" → "Marten" saiu); sobram "grey" → "gray" (variante de grafia), "tics" → "ticks" e "shott" → "shot" (grafia antiga) |
      | Audiolivro, 179 frases (texto certo), com o gemma | 0 trocas, 41 s | 0 trocas, 26 s |
      | 49 gravações, `avaliar-fala --corrigir` | WER 6,3% → 5,5%, 5 certas, 0 erradas | o mesmo, 10,1 s por frase |

      Audiolivro de 17 min processado com as opções padrão (`arquivo`, transcrição do cache, WhisperLiveKit no ar): a
      mesma única troca ("marvellous" → "marvelous"), em 112,4 s contra 149,5 s antes. O custo do BERT por
      palavra quase dobra para palavras de vários tokens (lote de 2n−1 em vez de n); nos exemplos em português, só o
      BERT foi de 70 s para 86 s.
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
      para cada gravação; num arquivo, o modelo carrega uma vez.
      **Memória (medida em 2026-11-23):** pico do processo do Cli no audiolivro (transcrição em cache, só os
      homófonos, um idioma carregado — inglês): **683–684 MB** com o BERT contra 37 MB sem ele, ou seja **~0,65 GB
      de RAM por idioma carregado**; não usa GPU. Duas rodadas, 21–23 s cada. Os dois idiomas juntos: não medido
      (cada modelo só carrega na primeira frase do seu idioma).
    - Por isso `CorrigirHomofonos` passou a ligada e, desde 2026-11-23, roda mesmo com a correção pelo modelo
      desligada. Só os homófonos (antes de a correção pelo modelo ser ligada por padrão):

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
    faz sentido. Ficou ligada, com limite 0,5.
  - **Decisão (2026-11-23, revogada em 2026-11-24 pela "4ª rodada"): `CorrigirTranscricao` passou a ligada por padrão.** Com a conferência pelo BERT e no
    áudio, a correção não errou nenhuma troca nas 49 frases (4 certas) e no audiolivro só mudou uma grafia
    ("marvellous" → "marvelous"), em 101 s para 17 min. Continua corrigindo poucas das palavras erradas (ver abaixo);
    o ganho é pequeno, mas sem estrago medido. Custa o `gemma3:12b` no Ollama (8,2 GB, baixado pelo
    `preparar-ambiente.ps1`; `-SemCorrecao` não baixa) e ~3,5 s por frase duvidosa. Como o resultado sem erro depende
    da conferência, com `ConferirCorrecaoComBert` ligada a correção pelo modelo só roda no idioma que tem o BERT (num
    clone preparado com `-SemHomofonos`, não roda; sem a conferência eram 4 certas e 1 errada). Conferido depois da troca: o
    `arquivo` com as opções padrão numa gravação nova passou pela correção (chave do cache
    `corr.ollama_gemma3_12b_r4-cb_0.5-h3_0.99`).
  - As contagens das variantes "BERT no lugar do modelo" vêm de uma rodada em que o "antes" já incluía os homófonos
    (cada uma contou "cela" → "sela" como errada, sem efeito); a tabela já desconta isso.
- **Precisão baixa em texto já certo:** o modelo "corrige" palavras que só são raras ou literárias. A 2ª chamada
  cortou o dano no audiolivro de 19 para 5 trocas (~0,2 ponto de WER) sem perder ganho na fala do usuário (−0,9 ponto),
  mas a correção ainda acerta pouco: 2 das 17 palavras erradas. Por isso ficou desligada até 2026-11-23, quando a
  conferência pelo BERT zerou as trocas erradas (decisão acima).
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
| Memória da VM do WSL | 16 GB + 4 GB de swap (`.wslconfig`; até 2026-11-22, 12 GB) | 10 GB ou mais (com 4 GB o Ollama trava ao trocar de modelo); 16 GB numa máquina de 32 GB |
| RAM do programa que usa a lib (fora do WSL) | ~0,7 GB de pico com o BERT dos homófonos (um idioma) | +0,65 GB por idioma do BERT carregado; sem os homófonos, poucas dezenas de MB |
| Disco para o Docker | ~75 GB usados | 50 GB livres na 1ª preparação (pode ficar em outro disco) |
| .NET SDK | 10.0.400 | 10.0 |
| ffmpeg/ffprobe | 9.0 (gyan.dev) | qualquer versão recente no PATH |

### Uso de VRAM

Os serviços dividem a mesma GPU. Medido em 2026-11-21:
- tempo real (WhisperLiveKit turbo) + `translategemma:4b` + Speaches ocioso: ~5–9 GB;
- separador de voz (Demucs `htdemucs_ft`): ~1,3 GB com o modelo carregado; a lib o tira da GPU antes do Whisper. No
  filme de 2 h, o pico da GPU inteira foi 6,7 GB (2026-11-25);
- arquivo com análise de sentido: `translategemma:12b` e `gemma3:12b` (~8 GB cada) são usados um depois do outro;
  rode com o WhisperLiveKit parado se a GPU tiver 12 GB. Desde 2026-11-25 a lib faz isso sozinha: o tempo real fica
  parado até uma sessão abrir, e o arquivo espera a sessão fechar (manual, seção 8).

Medido em 2026-11-23, 1.500 frases do `avaliar-homofonos --com-modelo` (31 min) com o `gemma3:12b` e o WhisperLiveKit
no ar, VM do WSL com 12 GB:
- GPU: 11,7–12,0 de 12,3 GB (o `gemma3:12b` inteiro na GPU, 7,5 GB);
- RAM do contêiner do Ollama: 8,5–9,3 GB, quase tudo cache do arquivo do modelo (o Ollama lê o modelo por mmap);
  WhisperLiveKit ~0,6 GB, Speaches ~0,3 GB. No fim, 2 GB disponíveis na VM e 1,6 GB de swap em uso;
- carregando o modelo sem mmap (`use_mmap: false`) o Ollama fica com 4,8 GB, mas a carga passa de 6,5 s para 21,8 s;
  não adotado, porque a lib descarrega e recarrega os modelos entre as etapas.

Nessa medição não travou, mas uma vez, num processamento longo nas mesmas condições, o Docker Desktop perdeu a VM
("no route to host") sem aviso nos logs; a causa não foi confirmada. Desde então a VM tem 16 GB (`.wslconfig`).

O Ollama descarrega um modelo depois de 10 min sem uso (`KeepAlive`), e o Speaches depois de 5 min (`WHISPER__TTL` no
`docker-compose.yml`; até 2026-11-22 o arquivo usava `STT_MODEL_TTL`, nome que a v0.8 ignora, e valia o padrão de 300 s
do mesmo jeito). No processamento de arquivo a lib não espera esses tempos (abaixo).

### Contêineres ligados sob demanda (2026-11-25)

Parados sem modelo carregado, os contêineres ainda ocupavam RAM da VM do WSL: Speaches ~0,8 GB, Ollama ~0,7 GB e
separador ~0,3 GB (`docker stats`). Agora ficam parados, e a lib liga cada um na primeira chamada e o desliga depois
de 5 min sem uso (`Transcritor:Conteineres`, manual seção 8). Tempo do `docker start` até o serviço responder, medido
com a GPU livre, duas vezes cada:

| Contêiner | 1ª subida | 2ª subida | Rota conferida |
|---|---|---|---|
| speaches | 16,8 s | 5,8 s | `/health` |
| ollama | 16,5 s | 7,6 s | `/api/version` |
| separador | 6,0 s | 3,9 s | `/health` |

A carga do modelo vem depois e não mudou (Whisper ~5–9 s; modelo de 4 GB do Ollama ~4 s). Parar os três juntos levou
4,0 s. Com tudo parado, o `servicos` do Cli levou 36,6 s (liga os três, confere e para). Um áudio de 9 s com correção e
tradução levou 164 s; o mesmo pedido com os contêineres já no ar não foi medido.

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
