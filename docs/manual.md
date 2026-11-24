# Manual da Transcritor lib

Passo a passo para preparar a máquina, testar pelo console e usar a lib nos seus projetos.
Modelos, números e requisitos testados: [modelos.md](modelos.md).

Sumário:
1. [O que a lib faz](#1-o-que-a-lib-faz)
2. [Instalar os pré-requisitos](#2-instalar-os-pré-requisitos)
3. [Baixar o código e preparar o ambiente](#3-baixar-o-código-e-preparar-o-ambiente)
4. [Usar pelo console (Cli)](#4-usar-pelo-console-cli)
5. [Usar a lib no seu projeto](#5-usar-a-lib-no-seu-projeto)
6. [Configuração](#6-configuração)
7. [Cache](#7-cache)
8. [GPU e memória](#8-gpu-e-memória)
9. [Problemas comuns](#9-problemas-comuns)
10. [Atualizar](#10-atualizar)

---

## 1. O que a lib faz

| Capacidade | O que entrega | Serviço por trás |
|---|---|---|
| Arquivo | Transcreve áudio ou vídeo e grava um JSON com frases e palavras, cada uma com início e fim; opcionalmente traduz cada frase e explica as expressões | Speaches + Ollama |
| Microfone em tempo real | Eventos `Provisorio` (texto que ainda muda) e `Confirmado` (texto final), com tradução | WhisperLiveKit + Ollama |
| Som do sistema em tempo real | O mesmo, com o que está tocando no computador | WhisperLiveKit + Ollama |
| Tradução | Português ↔ inglês pelo sentido: frase com as vizinhas como contexto; palavra solta com a frase | Ollama |
| Homófonos (com o dicionário e os modelos BERT) | Troca a grafia de palavras que soam igual pela que o sentido pede ("a cela do cavalo" → "a sela do cavalo"), antes de traduzir | BERT na CPU + dicionário |
| Correção da transcrição (ligada por padrão) | Troca palavras que o reconhecimento ouviu errado por outra de som parecido, pelo sentido da frase ("night" → "knight"), antes de traduzir | Ollama + dicionário |
| Dicionário local | Sentidos reais de uma palavra ou expressão, inglês ↔ português, com a forma base das flexões ("gave" → "give") | SQLite gerado do Wiktionary |

Nos três modos você escolhe a saída: `Portugues`, `Ingles` ou `Ambos`. O texto sai sempre no idioma falado; a
tradução vem quando a saída pede o outro idioma.

Tudo roda local, de graça, na sua GPU. Os modelos ficam em contêineres Docker; a lib só faz as chamadas.

A lib não tem interface com o usuário. Ela entrega dados (JSON, eventos e objetos .NET); exibir legenda, tocar
o vídeo ou traduzir a palavra que a pessoa escolheu na tela é papel do app que usa a lib (outro projeto).

## 2. Instalar os pré-requisitos

Precisa de Windows 10/11 com GPU NVIDIA (12 GB recomendado; ver [modelos.md](modelos.md#requisitos-de-hardware-e-software-testado)).

1. **Driver NVIDIA** 576 ou mais novo (pelo NVIDIA App ou nvidia.com). Confira com `nvidia-smi`.
2. **Docker Desktop** com backend WSL2: <https://www.docker.com/products/docker-desktop/>.
   - Em *Settings › Resources › Advanced › Disk image location*, deixe o disco do Docker numa unidade com
     **60 GB livres** ou mais (a 1ª preparação baixa ~50 GB; ~58 GB com `-ComCorrecao`, ~48 GB com `-SemDicionario -SemHomofonos`).
3. **Memória da VM do WSL**: se existir `%USERPROFILE%\.wslconfig` com `memory` abaixo de 10 GB, aumente (abaixo,
   16 GB + 4 GB de swap, para máquina com 32 GB de RAM; com menos, 12 GB) e rode `wsl --shutdown`; com 4 GB o Ollama
   trava ao trocar de modelo. Com 12 GB a VM fica no limite quando o `gemma3:12b` e o WhisperLiveKit estão no ar
   (ver [modelos.md](modelos.md#uso-de-vram)).
   ```ini
   [wsl2]
   memory=16GB
   swap=4GB
   ```
4. **.NET SDK 10**, **ffmpeg** e **Git**:
   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```
   ```powershell
   winget install Gyan.FFmpeg
   ```
   ```powershell
   winget install Git.Git
   ```
   Abra um terminal novo depois de instalar, para o PATH valer.

## 3. Baixar o código e preparar o ambiente

1. Clone o repositório:
   ```powershell
   git clone https://github.com/richarddw1988/transcritor-lib
   ```
   ```powershell
   cd transcritor-lib
   ```
2. Abra o Docker Desktop e espere ele ficar pronto.
3. Rode o script de preparação:
   ```powershell
   .\scripts\preparar-ambiente.ps1
   ```
   Se o PowerShell bloquear scripts:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\preparar-ambiente.ps1
   ```

O script é idempotente (pode rodar quantas vezes quiser, só faz o que falta):
1. confere .NET 10, ffmpeg/ffprobe, GPU e driver, Docker, espaço do disco do Docker e memória do WSL;
2. constrói a imagem do Speaches (a oficial 0.8.3 com uma correção, ver seção 8) e a do WhisperLiveKit (não há
   imagem publicada; ~22 GB, 20+ min na 1ª vez) e sobe os serviços;
3. espera cada serviço responder, confere se os contêineres enxergam a GPU e se o Speaches no ar é a imagem do
   projeto (aviso, se não for: a lib não consegue tirar o Whisper da GPU entre as etapas);
4. baixa os modelos que faltam (Whisper no Speaches; translategemma 12b e 4b, gemma3 4b e, para a correção, gemma3
   12b no Ollama);
5. compila a solução e roda `transcritor servicos`;
6. baixa o Wiktionary e as listas de frequência e gera o dicionário (`dados/dicionario.db`; `-SemDicionario` pula);
7. converte para ONNX, num contêiner, os dois BERT dos homófonos e da conferência da correção (`dados/bert`;
   `-SemHomofonos` pula). Sem eles, a correção pelo modelo não roda (5.5).

A pasta `dados/` fica dentro do clone: um clone novo precisa dos passos 6 e 7 de novo (o resto fica no Docker).

No fim deve aparecer **Ambiente pronto**. Na 1ª vez leva de 20 a 60 min, conforme a internet.

Opções do script:

| Opção | Efeito |
|---|---|
| `-SoConferir` | Só confere; não constrói, não sobe, não baixa |
| `-SemTempoReal` | Não constrói nem sobe o WhisperLiveKit (economiza ~22 GB); arquivo e tradução continuam |
| `-ComAnaliseDeSentido` | Também baixa o `gemma3:12b` (análise de sentido, ~8 GB) |
| `-ComCorrecao` | Também baixa o `gemma3:12b` da correção de palavras ouvidas errado (~8 GB; o mesmo da análise) e confere os serviços com ela. A correção vem desligada desde 2026-11-24; para usá-la, ligue `CorrigirTranscricao` e `ConfirmarHomofonoComModelo` no `appsettings.json` (seção 6) ou passe `--corrigir` ao `arquivo` |
| `-ModeloTranscricao`, `-ModeloFrases`, `-ModeloFrasesTempoReal`, `-ModeloPalavras`, `-ModeloAnalise`, `-ModeloCorrecao` | Usa outros modelos (ponha os mesmos no `appsettings.json` do seu app) |
| `-SemDicionario` | Não baixa o Wiktionary (~600 MB, kaikki.org) nem as listas de frequência (~1,3 MB) e não gera `dados/dicionario.db` (~60 MB, ~40 s); só informa. Sem ele, os homófonos não rodam e a correção fica sem as palavras parecidas. Sem a opção, o script também gera de novo o dicionário de versão anterior. Ver 4.6 |
| `-SemHomofonos` | Não gera `dados/bert` (~1 GB: os BERT do huggingface.co convertidos para ONNX num contêiner `python:3.12-slim` por `scripts/exportar-bert.py`; ~1,3 GB de download e ~2,5 min na 1ª vez; não precisa de Python no Windows); só informa. Sem eles, nem os homófonos nem a correção pelo modelo rodam (5.5) |

4. Rode os testes (não precisam dos serviços):
   ```powershell
   dotnet test
   ```

Os serviços sobem sozinhos com o Docker Desktop (`restart: unless-stopped`). Para parar ou subir à mão:
`docker compose stop` / `docker compose up -d`.

## 4. Usar pelo console (Cli)

Todos os comandos: `dotnet run --project src/Transcritor.Cli -- <comando> [opções]`. Abaixo, `transcritor` abrevia
isso. A configuração do Cli fica em `src/Transcritor.Cli/appsettings.json`.

### 4.1 Conferir os serviços
```powershell
dotnet run --project src/Transcritor.Cli -- servicos
```
Mostra `[ OK ]` ou `[FALHA]` com o motivo para ffmpeg, Speaches, Ollama (e modelos) e WhisperLiveKit.
Para tirar os modelos da GPU (ex.: antes de um jogo ou de outro programa pesado), veja a seção 4.9.

### 4.2 Transcrever (e traduzir) um arquivo
```powershell
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos
```
- Grava `C:\videos\aula.mp4.transcricao.json` e guarda no cache (repetir responde em ~0,1 s, sem chamar serviços).
- Mostra idioma, duração, quantidade de frases e palavras, e as 5 primeiras frases com tradução.
- Sem `--idioma`, a lib confere a detecção do Whisper entre português e inglês nos primeiros 30 s
  (`ConferirIdioma`, seção 6). Com sotaque, a detecção sozinha às vezes erra e devolve a fala já traduzida.
- `--idioma pt` força o idioma (pula a detecção e a conferência); `--json saida.json` grava uma cópia (só com um
  arquivo); `--modelo` troca o Whisper.
- **Nomes e termos do assunto** (pessoas, lugares, siglas, jargão): ponha um por linha (ou separados por vírgula) em
  `C:\videos\aula.mp4.vocabulario.txt`, ao lado da mídia, ou passe `--vocabulario termos.txt` para todos os arquivos
  da execução (os dois somam; linhas com `#` são comentário). A lista vai ao Whisper como dica. No FLEURS, com ~15
  termos por grupo de 10 frases, os termos escritos certo foram de 86% para 97% em português e de 81% para 95% em
  inglês, e o WER caiu de 4,3% para 3,1% e de 8,0% para 4,1% (`docs/modelos.md`). Só ajuda o termo que está na lista;
  até 800 caracteres (o resto fica de fora). Mudar a lista gera resultado novo no cache.
- Vários arquivos de uma vez (`arquivo aula1.mp4 aula2.mp4 aula3.mp4`) processam em lote: cada etapa roda para todos
  antes da seguinte (transcreve todos, depois traduz todos), e cada modelo carrega uma vez por lote em vez de uma vez
  por arquivo. O JSON de cada um fica ao lado da mídia; o progresso mostra `[2/3]`. Se parar no meio, rodar de novo
  continua de onde parou (cada etapa vai para o cache assim que termina). Medido com 10 gravações curtas: ~91 s em
  lote contra ~486 s um por vez (cada arquivo recarrega o Whisper e o modelo da tradução); ver `docs/modelos.md`.
- `--sentido` também gera os blocos (expressões) e o "o que quis dizer" de cada frase. É lento (~10–13 s por frase)
  e precisa do `gemma3:12b` (`preparar-ambiente.ps1 -ComAnaliseDeSentido`); com 12 GB de GPU, pare o tempo real antes
  (`docker stop whisperlivekit`).
- Os homófonos pelo BERT ("cela" → "sela") rodam sempre que o dicionário e os modelos BERT existem (5.5).
- `--corrigir` liga a correção de palavras ouvidas errado pelo `gemma3:12b` antes de traduzir (`CorrigirTranscricao`
  e `ConfirmarHomofonoComModelo`, seção 6; precisa de `preparar-ambiente.ps1 -ComCorrecao`). Vem desligada desde
  2026-11-24: nas 49 frases gravadas pelo usuário levou o WER de 6,3% a 5,5%, mas no FLEURS, no CORAA e no AMI não
  mudou o WER e custou de 9 a 33 s por frase (ver `docs/modelos.md`). Só frases com alguma palavra de confiança abaixo
  de 0,95 vão ao modelo, e cada troca proposta é conferida pelo BERT e no áudio (a frase é transcrita de novo). A
  palavra trocada guarda a original em `ouvida` no JSON.

Tempo de referência (RTX 3060): 6 min de áudio em inglês → ~8 min com a tradução 12b (7 min só da tradução), antes de
a correção ser ligada por padrão (ela soma ~100 s por 17 min de audiolivro);
~16 min com `--sentido`. Só na 1ª vez; depois vem do cache.

Os exemplos de código da seção 5 estão também em `tests/Transcritor.Tests/ExemplosDoManualTests.cs`, que precisa
compilar: se a API mudar, o build quebra até o manual ser atualizado.

### 4.3 Traduzir uma frase ou uma palavra
```powershell
dotnet run --project src/Transcritor.Cli -- traduzir "O banco fecha às quatro." --de pt --para en --antes "Preciso pagar umas contas."
```
```powershell
dotnet run --project src/Transcritor.Cli -- palavra banco --frase "O banco foi pintado de azul." --de pt --para en --traducao-frase "The bench was painted blue."
```
`--antes`/`--depois` dão contexto (podem repetir). Na palavra, `--traducao-frase` (o campo `traducao` do JSON) é a
melhor pista do sentido; sem ela a lib traduz a frase antes.

### 4.4 Tempo real: microfone e som do sistema
1. Teste a captura sozinha (sem o WhisperLiveKit) e veja o nível do áudio:
   ```powershell
   dotnet run --project src/Transcritor.Cli -- captura --fonte Microfone --segundos 5
   ```
   Os segundos contam a partir do aviso "Gravando: pode falar" (o dispositivo leva um instante para abrir;
   falar antes corta o começo). Pico abaixo de ~10%: aproxime-se do microfone ou suba o volume de gravação no Windows.
   Com `--ate-enter`, grava até você apertar Enter (no máximo `--segundos`); `--wav arquivo.wav` salva o áudio.
2. Microfone:
   ```powershell
   dotnet run --project src/Transcritor.Cli -- microfone --saida Ambos --idioma pt --contexto "Dona Chica, Luis Barcellos"
   ```
3. Som do sistema (toque um vídeo):
   ```powershell
   dotnet run --project src/Transcritor.Cli -- sistema --saida Ambos
   ```
4. Ctrl+C encerra; os últimos trechos ainda são confirmados e traduzidos.

O que aparece:
- linha cinza `… texto`: provisório (ainda pode mudar);
- `CONF #n [idioma] (início–fim) texto`: trecho confirmado, ~3 s depois da fala;
- `TRAD #n → tradução`: a tradução do mesmo trecho `#n`, ~1–2 s depois.

Dicas de qualidade (medidas em [pesquisa-asr.md](pesquisa-asr.md)):
- **Use `--idioma pt`** para fala em português; a detecção automática puxa para o inglês no começo.
- **Use `--contexto`** com nomes e termos esperados. A lib põe maiúscula e ponto final, porque o Whisper imita o estilo
  do contexto (sem pontuação ele deixa de pontuar).
- Música cantada, risadas e várias vozes juntas são difíceis para qualquer modelo.

### 4.5 Testar o tempo real sem falar
```powershell
dotnet run --project src/Transcritor.Cli -- simular C:\videos\aula.mp4 --saida Ambos --idioma pt
```
Toca o arquivo para o WhisperLiveKit no ritmo real (`--velocidade 2` = dobro). Serve para comparar configurações no
mesmo áudio.

### 4.6 Dicionário inglês ↔ português
Dicionário local gerado do Wiktionary (extração do kaikki.org, licença CC BY-SA 4.0 / GFDL), com a posição de cada
palavra em listas de frequência ([FrequencyWords](https://github.com/hermitdave/FrequencyWords), contagens de
legendas do OpenSubtitles 2018, CC BY-SA 4.0), que dizem quais palavras são raras. Não usa GPU nem serviços. A pasta
`dados/` não vai para o Git.

1. O `.\scripts\preparar-ambiente.ps1` já faz isto (sem `-SemDicionario`). À mão: baixe os dois dumps (~600 MB) e
   as duas listas (~0,6 MB cada) e gere o SQLite:
   ```powershell
   New-Item -ItemType Directory -Force dados
   ```
   ```powershell
   curl.exe -L -o dados\kaikki-en.jsonl.gz https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl.gz
   ```
   ```powershell
   curl.exe -L -o dados\kaikki-pt.jsonl.gz https://kaikki.org/dictionary/Portuguese/kaikki.org-dictionary-Portuguese.jsonl.gz
   ```
   ```powershell
   curl.exe -L -o dados\en_50k.txt https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_50k.txt
   ```
   ```powershell
   curl.exe -L -o dados\pt_br_50k.txt https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/pt_br/pt_br_50k.txt
   ```
   ```powershell
   dotnet run --project src/Transcritor.Cli -- dicionario importar
   ```
   `--ingles`, `--portugues`, `--frequencia-ingles`, `--frequencia-portugues` e `--banco` trocam os caminhos
   (padrões: `dados\kaikki-en.jsonl.gz`, `dados\kaikki-pt.jsonl.gz`, `dados\en_50k.txt`, `dados\pt_br_50k.txt` e
   `dados\dicionario.db`). Sem as listas de frequência a importação funciona, avisa e a dica de palavras raras
   (abaixo) não pode ser ligada. A importação também guarda os homófonos que o Wiktionary lista ("cela" ↔ "sela",
   "bare" ↔ "bear"), usados pela correção (seção 5.5); dicionário gerado antes de 2026-11-23 não tem essa lista e
   continua funcionando sem ela; para ter, rode o `dicionario importar` de novo (~40 s).
2. Consulte:
   ```powershell
   dotnet run --project src/Transcritor.Cli -- dicionario procurar gave --de en --para pt
   ```
   Mostra cada sentido com a classe e o sentido em inglês, por exemplo `give (de give) · verbo: dar [transfer the
   possession of something to someone else]`. Aceita expressões (`"give up"`, `"call it a day"`). Maiúsculas e
   pontuação em volta são ignoradas. No inglês, sem verbete para o possessivo, procura sem o `'s` ("nation's").
3. Palavras de som parecido (para corrigir o que o reconhecimento de fala ouviu errado):
   ```powershell
   dotnet run --project src/Transcritor.Cli -- dicionario parecidas weather --idioma en
   ```
   Lista palavras do dicionário (e flexões) com chave de pronúncia igual ou a uma letra de distância, ordenadas pela
   soma das distâncias de pronúncia e de escrita e, no empate, pelas mais comuns. `--maximo` muda quantas aparecem
   (padrão 10). Pronúncia: Double Metaphone no inglês (biblioteca Phonix, Apache 2.0) e Metaphone pt-BR no português
   (biblioteca MetaphonePtBr, MIT).
4. Homófonos (palavras que soam igual, segundo o Wiktionary):
   ```powershell
   dotnet run --project src/Transcritor.Cli -- dicionario homofonos cela --idioma pt
   ```
   Mostra `sela`. Dicionário gerado antes de 2026-11-23 não tem a lista: o comando avisa e sai com código 4.

Dicionário gerado por uma versão anterior da lib é recusado com a mensagem de gerar de novo: rode
`transcritor dicionario importar` (os dumps baixados continuam valendo). A lib, ao achar um dicionário de outra
versão pela opção `CaminhoDicionario`, segue sem ele; o `preparar-ambiente.ps1` avisa nesse caso.

O que tem e quanto custa: [modelos.md](modelos.md#dicionário-local-wiktionary).

Com o dicionário gerado, a tradução de palavra (comando `palavra` e `TraduzirPalavraAsync`) passa a usá-lo, pela
opção `CaminhoDicionario` (seção 6), que no Cli já aponta para `dados/dicionario.db`. O dicionário só entra quando a
palavra faz parte de uma expressão dele ("gave" em "gave up", "running" em "running out of"): o modelo recebe os
sentidos da expressão como opções numeradas, marcando os que aparecem na tradução da frase (a passada em
`--traducao-frase` ou, sem ela, a que a lib fez), e escolhe um. Palavra fora de expressão vai só com a frase e a
tradução dela: as opções da palavra sozinha faziam o modelo escolher sentidos raros de palavras comuns.

Experimental, desligado por padrão: com as listas de frequência e a opção `LimiarPalavraRara` (seção 6) ligada, a
tradução das frases do arquivo (comando `arquivo` e `TraduzirFrasesAsync`) também usa o dicionário: as palavras da
frase fora das N mais frequentes vão com as traduções do dicionário nas instruções do modelo ("porcupine":
porco-espinho, ouriço…). Nomes e contrações ficam de fora. A frase avulsa (comando `traduzir`, tempo real) não usa.
Medido com 10.000 num audiolivro: acerta vocabulário raro, mas às vezes muda outra parte da frase ("peach tree" →
"macieira"); ver [modelos.md](modelos.md#dicionário-na-tradução-de-frases-experimental-desligado-por-padrão).

### 4.7 Medir a tradução de palavra
```powershell
dotnet run --project src/Transcritor.Cli -- avaliar-palavras docs/avaliacao/palavras-ambiguas.json
```
Traduz cada caso do arquivo (palavra, frase, tradução da frase, idiomas e respostas aceitas), sem cache, e mostra
acertos e tempo. `--sem-dicionario` desliga o dicionário; `--sem-traducao-frase` não passa a tradução da frase.
Uma resposta conta como certa se contém uma das `aceitas`.
Há três conjuntos: `palavras-ambiguas.json` (32 casos, em que as regras do dicionário foram ajustadas),
`palavras-validacao.json` (78 casos, que sugeriram a regra atual mas não serviram para decidi-la) e
`palavras-teste.json` (82 casos, onde a regra atual foi decidida). Para conferir uma mudança, rode os três e não ajuste
nada olhando os erros de um conjunto que você vai usar para decidir.

### 4.8 Medir o reconhecimento da sua fala
1. Grave as frases de `docs/avaliacao/frases-para-gravar.json` pelo microfone: mostra cada frase, Enter começa,
   fale no seu ritmo e aperte Enter de novo quando terminar (não há tempo fixo). Salva em `dados/gravacoes/<id>.wav`,
   fora do Git. Frases já gravadas são puladas; `-Regravar` refaz todas e `-Somente en31,en32` grava só estas.
   São 49 frases: as 20 primeiras (en01–en12, pt01–pt08) e, desde 2026-11-22, mais 29 em grupos: inglês e
   português curtos ou cheios de palavras do outro idioma (onde a detecção de idioma erra), palavras de som parecido
   (correção), palavras raras (dica do dicionário) e quatro falas mais longas (en31, en32, pt16, pt17). Só as gravadas
   entram na medição.
   ```powershell
   .\scripts\gravar-frases.ps1
   ```
   Até 2026-11-22 o script gravava por um tempo fixo (0,45 s por palavra + 3 s) e cortava as frases longas: a en31
   foi cortada assim (regrave com `-Somente en31,en32`).
2. Meça:
   ```powershell
   dotnet run --project src/Transcritor.Cli -- avaliar-fala
   ```
   Transcreve cada gravação no idioma da frase, alinha com o texto certo e mostra cada troca, falta e sobra de
   palavra, com a confiança da palavra ouvida e em que posição a palavra certa aparece entre as 15 parecidas do
   dicionário (`dicionario parecidas`). No fim: WER (taxa de erro por palavra), quantas palavras erradas ficaram abaixo
   de `--limite-confianca` (padrão 0,5) e quantas certas também ficaram (alarmes falsos). `--frases`, `--pasta` e
   `--banco` trocam os caminhos. Esta medida não passa pela correção pelo modelo, mesmo com ela ligada (ver o passo 3);
   os homófonos, sim.
   - O Whisper escreve números como dígitos ("sete" → "7"), o que conta como troca: evite números nas frases.
   - Se você leu a frase diferente (repetiu palavra, trocou o tempo verbal), ponha o que disse de fato no campo
     opcional `falado` da frase no JSON: a medida compara com ele, e o `texto` continua sendo o que o script mostra.
     Erro de pronúncia (a palavra certa, dita de um jeito que soa como outra) fica no `texto`: é o que se quer medir.
3. Meça a correção de palavras ouvidas errado (`ModeloCorrecao`, com o dicionário):
   ```powershell
   dotnet run --project src/Transcritor.Cli -- avaliar-fala --corrigir
   ```
   Mostra cada troca feita (marcando as que pioraram a frase) e, no fim, o WER antes e depois e o tempo por frase.
   O "antes" é a transcrição crua, sem homófonos, para cada troca contar uma vez.
   Usa o mesmo caminho do arquivo (correção pelo texto + conferência no áudio). Para comparar variações, use as
   variáveis de ambiente, ex.: `$env:TRANSCRITOR_Transcritor__Arquivo__ConferirCorrecaoNoAudio = "false"`.
4. Meça a escolha de homófonos num texto (sem áudio):
   ```powershell
   dotnet run --project src/Transcritor.Cli -- avaliar-homofonos frases.txt --idioma pt
   ```
   Uma frase por linha (UTF-8). Cada uma passa só pela escolha de homófonos (sem a correção pelo modelo, todas as
   palavras com confiança 1) e o comando lista cada troca e o total. Num texto já escrito certo, toda troca é
   indevida; num com a grafia errada de propósito, mostra quais ele conserta. `--com-modelo` liga
   `ConfirmarHomofonoComModelo` (seção 6); sem ela, o comando mede só o BERT, mesmo com a opção ligada no
   `appsettings.json`.

### 4.9 Liberar a GPU
```powershell
dotnet run --project src/Transcritor.Cli -- liberar-gpu
```
Tira da GPU todos os modelos carregados no Ollama e o Whisper do Speaches, e para o WhisperLiveKit (ele carrega o
modelo ao subir e só solta a memória parado; sessões de tempo real abertas são encerradas). Mostra a memória da GPU
antes e depois (pelo `nvidia-smi`, contando outros programas). Para o tempo real voltar: `docker start whisperlivekit`.
- `--manter-tempo-real` deixa o WhisperLiveKit de pé (só descarrega Ollama e Speaches).
- `--conteiner` troca o nome do contêiner do WhisperLiveKit (padrão `whisperlivekit`).
- Os serviços não precisam de nada para voltar: o Ollama e o Speaches carregam o modelo de novo na próxima chamada.

## 5. Usar a lib no seu projeto

Ainda não há pacote NuGet: referencie os projetos.

1. Adicione as referências (ajuste o caminho):
   ```xml
   <ItemGroup>
     <ProjectReference Include="..\transcritor-lib\src\Transcritor.Arquivo\Transcritor.Arquivo.csproj" />
     <ProjectReference Include="..\transcritor-lib\src\Transcritor.Traducao\Transcritor.Traducao.csproj" />
     <ProjectReference Include="..\transcritor-lib\src\Transcritor.TempoReal\Transcritor.TempoReal.csproj" />
     <!-- Opcional: dicionário inglês ↔ português local (seção 5.4): -->
     <ProjectReference Include="..\transcritor-lib\src\Transcritor.Dicionario\Transcritor.Dicionario.csproj" />
     <!-- Só no Windows (microfone/som do sistema); seu projeto precisa de net10.0-windows: -->
     <ProjectReference Include="..\transcritor-lib\src\Transcritor.Captura\Transcritor.Captura.csproj" />
   </ItemGroup>
   ```
2. Registre no DI e ligue a configuração (seções iguais às do `appsettings.json` do Cli):
   ```csharp
   services.AddTranscritorArquivo();
   services.AddTranscritorTraducao();
   services.AddTranscritorTempoReal();
   services.AddTranscritorCaptura(); // Windows
   services.Configure<OpcoesArquivo>(config.GetSection("Transcritor:Arquivo"));
   services.Configure<OpcoesTraducao>(config.GetSection("Transcritor:Traducao"));
   services.Configure<OpcoesTempoReal>(config.GetSection("Transcritor:TempoReal"));
   ```
   Ou direto no código: `services.AddTranscritorTraducao(o => o.OllamaUrl = new Uri("http://outra-maquina:11434"));`

### 5.1 Arquivo
```csharp
var transcritor = sp.GetRequiredService<ITranscritorArquivo>();
var progresso = new Progress<ProgressoProcessamento>(p => Console.WriteLine($"{p.Etapa} {p.Percentual:F0}%"));
ResultadoTranscricao r = await transcritor.ProcessarAsync(@"C:\videos\aula.mp4", Saida.Ambos, progresso, ct);
foreach (var frase in r.Frases)
    Console.WriteLine($"[{frase.Inicio:F1}] {frase.Texto} → {frase.Traducao}");

// Vários arquivos: uma etapa por vez para todos; um resultado por arquivo, na mesma ordem.
IReadOnlyList<ResultadoTranscricao> lote = await transcritor.ProcessarLoteAsync(
    [@"C:\videos\aula1.mp4", @"C:\videos\aula2.mp4"], Saida.Ambos, progresso, ct);

// Nomes e termos do assunto como dica ao Whisper: o vocabulário daquele vídeo, ao lado da mídia...
await File.WriteAllTextAsync(OpcoesArquivo.CaminhoVocabularioAoLado(@"C:\videos\aula.mp4"), "Dona Chica\nLuis Barcellos\nNAudio", ct);
// ...e/ou um vocabulário para todos os arquivos, no registro:
services.AddTranscritorArquivo(o => o.Vocabulario = "Speaches, WhisperLiveKit");
```
O JSON (contrato estável; ler com `JsonTranscricao.LerAsync`):
```json
{
  "arquivo": { "hash": "sha256…", "duracao": 373.15, "idioma": "en" },
  "frases": [
    { "inicio": 7.16, "fim": 13.36,
      "texto": "You will rejoice to hear that no disaster has accompanied…",
      "traducao": "A senhora certamente ficará feliz ao saber que…",
      "palavras": [ { "texto": "You", "inicio": 7.16, "fim": 7.64, "confianca": 0.998 } ],
      "sentido": "O autor quer tranquilizar a destinatária…",
      "blocos": [ { "palavraInicial": 19, "palavraFinal": 22, "texto": "with such evil forebodings.",
                    "traducao": "com tamanhas premonições negativas", "tipo": "colocacao",
                    "nota": "'forebodings' = pressentimentos" } ] }
  ]
}
```
- Em lote, `ProgressoProcessamento.Arquivo` e `TotalArquivos` dizem de qual arquivo é cada aviso de progresso.
  `ProcessarLoteAsync` tem implementação padrão no `ITranscritorArquivo` (um arquivo por vez), então quem implementa a
  interface não precisa escrever o lote.
- Frase = sentença completa (não corta em "Mrs." nem no meio da sentença); `traducao` só quando a saída pede.
  Título de capítulo ("Chapter Three", "Capítulo 2", "Part IV") vira uma frase sozinha, mesmo com pausa entre a
  palavra e o número ou sem ponto depois do número.
- Palavra que o Whisper devolve em dois pedaços, com o segundo colado e começando com sinal (" Super" + "-G.",
  " fazê" + "-lo", " 1" + ".400", " U" + ".S.", " d" + "'água", " 8" + "%"), vira uma palavra só em `palavras`
  ("Super-G.", com o tempo das duas e a menor confiança), e a frase não sai "Super -G." nem "1 .400".
- `sentido` e `blocos` só com `AnalisarSentido = true`; `palavraInicial/palavraFinal` são índices em `palavras`.
- `confianca` (0 a 1) é a probabilidade que o Whisper deu à palavra; baixa indica provável erro de reconhecimento.
  Não aparece quando o motor não informa, nem em JSON gravado antes deste campo existir (`Palavra.Confianca` nulo).
- `ouvida` só aparece na palavra que a correção trocou (pelo modelo ou pelos homófonos, seção 5.5): `texto` traz a corrigida
  e `ouvida` a que o Whisper escreveu (`{ "texto": "knight", "ouvida": "night", … }`). O `texto` da frase já vem corrigido.
- Formato detalhado: [etapa-2.md](etapa-2.md) (regra da saída) e [etapa-4.md](etapa-4.md) (blocos).
- Entre as etapas, o modelo de cada serviço sai da GPU antes de a próxima carregar o seu (`DescarregarModelosEntreEtapas`,
  seção 8). Um motor, tradutor, corretor ou analisador seu entra nisso se implementar `IDescarregavel`
  (`Task DescarregarAsync(CancellationToken)`, que não deve lançar exceção se o serviço não responder).

### 5.2 Tradução
```csharp
var tradutor = sp.GetRequiredService<ITradutor>();
string frase = await tradutor.TraduzirFraseAsync(
    new PedidoTraducaoFrase("O banco fecha às quatro.", "pt", "en", Anteriores: ["Preciso pagar contas."], Seguintes: []), ct);
string palavra = await tradutor.TraduzirPalavraAsync(
    new PedidoTraducaoPalavra("banco", Frase: "O banco foi pintado de azul.", Origem: "pt", Destino: "en", TraducaoDaFrase: "The bench was painted blue."), ct);
```
Termos com tradução fixa (glossário, seção 6) também podem ir no código:
```csharp
services.AddTranscritorTraducao(o =>
    o.Glossario.Add(new EntradaGlossario { Origem = "en", Termo = "Giant Peach", Traducao = "Pêssego Gigante" }));
```

### 5.3 Tempo real
```csharp
var tempoReal = sp.GetRequiredService<ITranscritorTempoReal>();
using var parar = new CancellationTokenSource(); // cancele para encerrar com calma
var sessao = new OpcoesSessaoTempoReal(Idioma: "pt", Contexto: "Dona Chica, Luis Barcellos");
await foreach (var e in tempoReal.IniciarAsync(Fonte.Microfone, Saida.Ambos, sessao, parar.Token))
{
    // Provisorio: texto que ainda pode mudar (Texto vazio = nada pendente).
    // Confirmado sem Traducao: trecho final. O MESMO Id chega de novo com Traducao preenchida.
}
```
- Cancelar o token para a captura; a sessão termina sem exceção depois de confirmar e traduzir o que faltava.
- Outra fonte de áudio (PCM s16le 16 kHz mono): implemente `IFonteAudio` e use `IniciarAsync(fonte, …)`;
  para testes há `FonteAudioWav`.
- Limites: tempo por trecho (não por palavra), tradução só com as frases anteriores como contexto. Ver [etapa-3.md](etapa-3.md).

### 5.4 Dicionário
Não usa DI nem serviços: abra o arquivo gerado na seção 4.6. É só leitura e pode ser usado por várias threads.
```csharp
using var dicionario = DicionarioSqlite.Abrir(@"dados\dicionario.db");
IReadOnlyList<Verbete> sentidos = dicionario.Procurar("gave", origem: "en", destino: "pt");
foreach (var v in sentidos)
    Console.WriteLine($"{v.Termo} ({v.Classe}): {v.Traducao} [{v.Sentido}]"); // give (verbo): dar [transfer the possession…]
```
- `Lema` vem preenchido quando o termo era uma flexão ("gave" → "give", "desisti" → "desistir").
- `Tipo`: `Traducao` (tradução direta, com o `Sentido` a que se refere) ou `Definicao` (palavra em português
  explicada em inglês, ex.: "pêssego" → "peach (fruit)"). As traduções vêm antes das definições.
- Arquivo ausente ou de outra versão: `FileNotFoundException`, com o comando para gerar.
- Para gerar pelo código: `ImportadorWiktionary.ImportarAsync(jsonlIngles, jsonlPortugues, caminhoBanco,
  new ListasFrequencia(@"dados\en_50k.txt", @"dados\pt_br_50k.txt"))`; sem o 4º argumento o banco fica sem frequência.
- Frequência: `dicionario.PosicaoFrequencia("nibbled", "en")` devolve a posição na lista (1 = a mais comum), a melhor
  entre a palavra e a forma base, ou nulo se não está na lista; `dicionario.TemFrequencia` diz se o banco tem as listas.
- Palavras de som parecido: `dicionario.ProcurarParecidas("weather", "en")` devolve `Parecida(Termo, DistanciaPronuncia,
  DistanciaEscrita, Sentidos)` — ex.: `feather`, `whether`, `water`…; a própria palavra não entra.
- Homófonos (soam igual, segundo o Wiktionary): `dicionario.Homofonos("cela", "pt")` devolve `["sela"]`;
  `dicionario.TemHomofonos` diz se o banco tem a lista (os gerados antes de 2026-11-23 não têm).

### 5.5 Correção da transcrição
O arquivo usa sozinho quando alguma parte está ligada: a correção pelo modelo (`CorrigirTranscricao`, desligada por
padrão desde 2026-11-24; precisa do `ModeloCorrecao` no Ollama e dos modelos BERT) e/ou os homófonos (`CorrigirHomofonos`, ligada, com o
dicionário e os modelos BERT). Para corrigir uma
frase avulsa (ex.: do tempo real):
```csharp
var corretor = sp.GetRequiredService<ICorretorTranscricao>();
IReadOnlyList<Correcao> trocas = await corretor.CorrigirAsync(
    new PedidoCorrecao("The night rode through the forest.".Split(' '), "en", Anteriores: [], Seguintes: []), ct);
foreach (var t in trocas)
    Console.WriteLine($"{t.Indice}: {t.Ouvida} → {t.Corrigida}"); // 1: night → knight
```
- O modelo devolve a frase corrigida, mas só valem trocas de uma palavra de 4+ letras, com confiança abaixo de
  `LimiteConfiancaCorrecao` (passe `Confiancas` no pedido), por outra de 4+ letras que soa parecido (chave de
  pronúncia igual ou a uma letra) e não é flexão dela ("take" → "taken"). Palavras acrescentadas, removidas ou de som
  diferente são ignoradas: a correção não reescreve a frase nem corrige gramática.
- No arquivo, cada troca que passa é conferida no áudio (`ConferirCorrecaoNoAudio`, ligada): a frase é transcrita de
  novo com a versão trocada como dica (`prompt` do Whisper), e a troca só vale se o Whisper devolver a palavra nova
  com confiança de pelo menos `LimiteConfiancaReouvir` (0,2). Quando o som não bate, o Whisper até copia a dica, mas
  com confiança perto de zero.
- `CorrigirAsync` sozinho não tem o áudio: para frase avulsa, ligue `ConfirmarCorrecaoComModelo`, uma 2ª chamada ao
  modelo que compara a frase com e sem a troca e só a aceita se a frase sem ela não fizer sentido.
- Medido nas 49 frases do usuário: 4 trocas certas e 0 erradas, com a conferência pelo BERT e no áudio; sem elas, o
  modelo sozinho troca palavras certas (ver `docs/modelos.md`). Ainda corrige poucas das palavras erradas.
- Com o dicionário (`CaminhoDicionario`), o modelo recebe as palavras parecidas de cada palavra como sugestão.
- Cada troca proposta pelo modelo também passa pelo BERT (`ConferirCorrecaoComBert`, ligada): só vale se ele der à
  palavra nova pelo menos `LimiteParecida` (0,5) contra a ouvida. Na fala do usuário, barrou a única troca errada
  ("leave" → "live") sem perder nenhuma certa. Por isso, com a opção ligada, **a correção pelo modelo só roda no idioma
  que tem o BERT** em `PastaModelosHomofonos` (gerado pelo `preparar-ambiente.ps1`); sem ele, a frase fica como foi
  ouvida e o Ollama nem é chamado. Para rodar sem o BERT, desligue `ConferirCorrecaoComBert` (mais trocas erradas).
- Homófonos (`CorrigirHomofonos`, ligada; precisa do dicionário com a lista de homófonos e dos modelos de
  `PastaModelosHomofonos`, senão não faz nada; roda mesmo com `CorrigirTranscricao = false`): depois da correção pelo
  modelo (se ligada), cada palavra que tem homófono no dicionário
  ("cela" ↔ "sela", "conserto" ↔ "concerto") passa por um BERT (modelo de lacuna, na CPU, sem Ollama nem GPU), que dá
  a probabilidade de cada grafia pelo sentido da frase. Troca só se outra grafia tiver pelo menos `LimiteHomofono`
  (0,99), mesmo que o reconhecimento estivesse seguro; palavra com maiúscula no meio da frase (nome) fica de fora. Se
  a mesma palavra aparece de novo na frase, a outra ocorrência vira lacuna na conta, para o BERT não copiá-la. A
  troca sai marcada (`Correcao.Homofono`) e, no arquivo, não é conferida no áudio, porque o som é o mesmo. Medido:
  "cela" → "sela" na fala do usuário e nenhuma troca indevida no audiolivro (ver `docs/modelos.md`).
- Com `ConfirmarHomofonoComModelo` (desligada por padrão desde 2026-11-24, junto com a correção), o homófono em que o BERT acha a outra grafia possível
  (entre `LimiteHomofonoComModelo`, 0,02, e 0,99) vai ao `ModeloCorrecao`, que vê a frase com cada grafia e diz qual
  está certa, duas vezes, com as opções em ordem trocada; só troca se escolher a outra grafia nas duas.
- `CorrigirAsync` faz as partes ligadas: com `CorrigirTranscricao = false`, só os homófonos. `Ativo` diz se alguma
  parte está ligada.

### 5.6 Erros
Todos herdam de `TranscritorException`:

| Exceção | Quando | O que fazer |
|---|---|---|
| `ServicoIndisponivelException` | Speaches, Ollama ou WhisperLiveKit não respondeu (`Servico`, `Url`) | `docker compose up -d <serviço>`; ver logs |
| `ModeloNaoInstaladoException` | O serviço respondeu, mas o modelo não está instalado; a mensagem traz o comando | rode o comando da mensagem ou o script |
| `FerramentaExternaException` | ffmpeg/ffprobe ausente ou falhou | `winget install Gyan.FFmpeg`, terminal novo |

## 6. Configuração

Seções do `appsettings.json` (ou variáveis de ambiente `TRANSCRITOR_Transcritor__Arquivo__SpeachesUrl` etc.).

**`Transcritor:Arquivo`**

| Chave | Padrão | O que é |
|---|---|---|
| `SpeachesUrl` | `http://localhost:8000` | Speaches |
| `ModeloTranscricao` | `Systran/faster-whisper-large-v3` | Whisper do arquivo |
| `Idioma` | `null` (automático) | Força `pt` ou `en` |
| `ConferirIdioma` | `true` | Sem `Idioma`, confere a detecção do Whisper: transcreve o começo do áudio também no outro idioma (português ↔ inglês) e fica com o que saiu com mais confiança por palavra. Evita a fala com sotaque voltar traduzida para o idioma errado. Custa uma transcrição curta a mais por arquivo (duas nos arquivos mais longos que a amostra). Só para pt/en: outro idioma detectado fica como veio |
| `SegundosConferirIdioma` | `30` | Tamanho da amostra do começo usada na conferência |
| `Vocabulario` | `null` | Nomes e termos do assunto (um por linha ou separados por vírgula) mandados ao Whisper como dica ("prompt") em todos os arquivos. Soma com o `<arquivo>.vocabulario.txt`. Até 800 caracteres. A conferência do idioma roda sem a dica; a transcrição final leva |
| `LerVocabularioAoLado` | `true` | Lê também `<arquivo>.vocabulario.txt` ao lado da mídia, se existir |
| `CaminhoFfmpeg` / `CaminhoFfprobe` | `ffmpeg` / `ffprobe` | Executáveis (PATH ou caminho completo) |
| `PastaCache` | `%LOCALAPPDATA%\Transcritor\cache` | Cache dos resultados |
| `GravarJsonAoLado` | `true` | Grava `<arquivo>.transcricao.json` ao lado da mídia |
| `TimeoutTranscricao` | `01:00:00` | Tempo máximo de uma transcrição |
| `RepararTrechosSuspeitos` | `true` | Transcreve de novo, isolado, cada trecho em que o Whisper se perdeu na transcrição longa — 3+ palavras com duração zero (alucinação/repetição) ou 15 s+ sem nenhuma pontuação — e troca se o resultado for melhor |
| `MaximoTrechosReparados` | `30` | Limite de trechos refeitos por arquivo (cada um é uma chamada a mais ao Speaches) |
| `ConferirCorrecaoNoAudio` | `true` | Com `CorrigirTranscricao`, transcreve de novo a frase de cada troca proposta, com a versão trocada como dica, e só aceita se o Whisper ouvir a palavra nova (seção 5.5) |
| `LimiteConfiancaReouvir` | `0.2` | Confiança mínima da palavra nova na frase reouvida |
| `DescarregarModelosEntreEtapas` | `true` | Ao fim de cada etapa (transcrição, propostas da correção, conferência no áudio, tradução, análise), tira da GPU o modelo usado antes de a próxima carregar o seu (seção 8) |
| `EsperaDescarregarTranscricao` | `00:00:20` | Quanto esperar o Whisper sair da GPU depois de pedir. Com a imagem original do Speaches 0.8 (sem o comando de descarregar), é o tempo máximo de espera pelo `WHISPER__TTL` |

**`Transcritor:Traducao`**

| Chave | Padrão | O que é |
|---|---|---|
| `OllamaUrl` | `http://localhost:11434` | Ollama |
| `ModeloFrases` | `translategemma:12b` | Frases do arquivo |
| `ModeloFrasesTempoReal` | `translategemma:4b` | Frases no tempo real e comando `traduzir` (vazio = `ModeloFrases`) |
| `ModeloPalavras` | `gemma3:4b` | Palavra solta |
| `CaminhoDicionario` | nulo (no Cli: `dados/dicionario.db`) | Dicionário da seção 4.6. Com ele, a palavra solta que faz parte de uma expressão do dicionário recebe os sentidos da expressão como opções numeradas e o modelo escolhe uma; nulo ou arquivo ausente = só o modelo |
| `LimiarPalavraRara` | `0` (desligado) | **Experimental.** Nas frases do arquivo, palavra fora das N mais frequentes (ex.: `10000`) vai com as traduções do dicionário nas instruções do modelo. Precisa do dicionário gerado com as listas de frequência (4.6). Ver 4.6 antes de ligar |
| `CorrigirTranscricao` | `false` | Corrige palavras ouvidas errado no arquivo pelo modelo de conversa, antes de traduzir (seção 5.5). Desligada desde 2026-11-24: quase não muda o WER e custa de 9 a 33 s por frase (`docs/modelos.md`). Precisa do `ModeloCorrecao` no Ollama (`preparar-ambiente.ps1 -ComCorrecao`) e, com `ConferirCorrecaoComBert`, do BERT do idioma (sem ele, não roda). Os homófonos não dependem dela |
| `ModeloCorrecao` | `gemma3:12b` | Modelo da correção |
| `LimiteConfiancaCorrecao` | `0.95` | Só palavras com confiança abaixo disto podem ser trocadas; frase sem nenhuma não vai ao modelo |
| `CorrigirHomofonos` | `true` | Cada palavra com homófono no dicionário ("cela" ↔ "sela") passa pelo BERT, mesmo com confiança alta e mesmo com `CorrigirTranscricao` desligada (5.5). Sem a lista de homófonos ou sem os modelos, não faz nada |
| `PastaModelosHomofonos` | nulo (no Cli: `dados/bert`) | Pasta dos BERT em ONNX (`en/` e `pt/`, cada uma com `model.onnx` e `vocab.txt`), gerados por `preparar-ambiente.ps1` (sem `-SemHomofonos`) |
| `ConferirCorrecaoComBert` | `true` | Cada troca proposta pelo modelo de conversa só vale se o BERT preferir a palavra nova (5.5). Ligada, a correção pelo modelo só roda no idioma com o BERT; desligada, roda sem conferir |
| `LimiteParecida` | `0.5` | Probabilidade mínima, pelo BERT, da palavra nova contra a ouvida; com 0,7 já perdeu uma troca certa |
| `LimiteHomofono` | `0.99` | Probabilidade mínima da outra grafia para trocar o homófono; abaixo de 0,99 entraram trocas erradas |
| `ConfirmarHomofonoComModelo` | `false` | Homófono em que o BERT dá à outra grafia entre `LimiteHomofonoComModelo` e `LimiteHomofono` vai ao `ModeloCorrecao`, que vê a frase com cada grafia e diz qual está certa, nas duas ordens; só troca se escolher a outra nas duas (e, com duas outras grafias possíveis, a que ganha da outra). Pega casos que o BERT não sabe ("Fomos ao conserto de música" → "concerto"); custa ~0,4 s por pergunta. Ainda troca a mais às vezes em texto coloquial ou literário ("pra trás" → "traz"). Desligada desde 2026-11-24 para o padrão não precisar do `gemma3:12b`. Ver `docs/modelos.md` |
| `LimiteHomofonoComModelo` | `0.02` | Probabilidade mínima, pelo BERT, da outra grafia para o homófono ir ao modelo de conversa |
| `ConfirmarCorrecaoComModelo` | `false` | 2ª chamada ao modelo por troca, comparando a frase com e sem ela. No arquivo a conferência no áudio mediu melhor; ligue ao usar `CorrigirAsync` em frase avulsa |
| `AnalisarSentido` | `false` | Liga blocos e "o que quis dizer" no arquivo |
| `ModeloAnalise` | `gemma3:12b` | Modelo da análise |
| `FrasesAntes` / `FrasesDepois` | `2` / `2` | Janela de contexto da tradução do arquivo |
| `KeepAlive` | `10m` | Quanto o Ollama mantém o modelo na memória |
| `Timeout` | `00:05:00` | Tempo máximo de uma chamada |
| `CaminhoCachePalavras` | `%LOCALAPPDATA%\Transcritor\cache\palavras.json` | Cache de palavras |
| `Glossario` | vazio | Termos com tradução fixa (nomes, títulos, termos técnicos), cada um com `Origem` (pt ou en), `Termo` e `Traducao`. Ver abaixo |

Glossário — exemplo no `appsettings.json`:
```json
"Glossario": [
  { "Origem": "en", "Termo": "Giant Peach", "Traducao": "Pêssego Gigante" },
  { "Origem": "en", "Termo": "Aunt Sponge", "Traducao": "Tia Esponja" }
]
```
- Vale nas frases (arquivo e tempo real) e na palavra solta. O termo é procurado como palavra inteira, sem diferenciar
  maiúsculas; havendo sobreposição, o mais longo vence ("Aunt Spiker" antes de "Spiker").
- A frase com termo vai ao modelo com a instrução de usar a tradução fixa. Se a tradução não trouxer o termo, a lib
  traduz de novo com o termo já trocado no texto; se ainda assim não vier, fica a 1ª tradução.
- Palavra pedida igual a um termo (ex.: `palavra "Giant Peach"`) devolve a tradução fixa sem chamar o modelo.
- Mudar o glossário invalida o cache de tradução dos arquivos e das palavras.
- Por variável de ambiente: `TRANSCRITOR_Transcritor__Traducao__Glossario__0__Termo=Giant Peach` (e `__Origem`, `__Traducao`).

**`Transcritor:TempoReal`**

| Chave | Padrão | O que é |
|---|---|---|
| `WhisperLiveKitUrl` | `ws://localhost:8001/asr` | WhisperLiveKit |
| `TimeoutConexao` | `00:00:10` | Tempo para conectar |
| `TimeoutFinalizacao` | `00:00:20` | Espera pelo fim da sessão depois de parar |
| `TrechosContexto` | `2` | Trechos anteriores usados como contexto da tradução |

O modelo do tempo real fica no `docker-compose.yml` (serviço `whisperlivekit`, `--model`); depois de mudar,
`docker compose up -d whisperlivekit`. Ali também ficam, como variáveis do serviço, os idiomas entre os quais a
detecção escolhe (`TRANSCRITOR_IDIOMAS`, padrão `pt,en`; o WhisperLiveKit original escolhia entre 99) e quanto de fala
ela espera antes de decidir (`TRANSCRITOR_SEGUNDOS_DETECCAO`, 2 s). Valem para o arquivo
`docker/whisperlivekit/align_att_base.py`, montado por cima do original da v0.2.27.

## 7. Cache

- Resultados de arquivo: um JSON por chave em `%LOCALAPPDATA%\Transcritor\cache`. A chave junta o hash do arquivo, o
  modelo, o vocabulário e as versões da montagem, do reparo de trechos, da correção (e da conferência no áudio), da tradução e da análise; trocar modelo ou prompt gera
  resultado novo.
- Palavras: `palavras.json` na mesma pasta.
- Para limpar: apague a pasta (os serviços refazem tudo na próxima vez).

## 8. GPU e memória

- Os três serviços dividem a GPU. Com 12 GB: tempo real + tradução 4b cabem juntos; a análise de sentido (12b) pede
  o tempo real parado (`docker stop whisperlivekit`; volta com `docker start whisperlivekit`).
- No processamento de arquivo, cada etapa tira o seu modelo da GPU antes da próxima (`DescarregarModelosEntreEtapas`,
  ligada): o Whisper sai depois da transcrição, o modelo da correção depois das propostas (a correção roda em duas
  fases: primeiro o modelo propõe as trocas de todas as frases, depois o Whisper confere cada uma no áudio), o da
  tradução depois da tradução. Antes de cada etapa, a lib também tira o modelo do outro serviço (o Whisper antes das
  etapas do Ollama e vice-versa), mesmo que ele tenha ficado na GPU de outra execução. Assim o Whisper (~3,9 GB) e um
  modelo de 8 GB do Ollama não disputam a placa.
- O Ollama tira o modelo na hora (`keep_alive` 0) e o Speaches também (`DELETE /api/ps`, ~0,4 s). No Speaches 0.8
  original esse comando trava o serviço; por isso o projeto usa uma imagem própria, a 0.8.3 com uma linha corrigida
  (`docker/speaches/Dockerfile`, construída pelo `preparar-ambiente.ps1`). A lib só manda o comando quando o `/health`
  do Speaches traz o cabeçalho `X-Transcritor-Descarregar` dessa imagem; com a original, espera o tempo ocioso
  (`WHISPER__TTL`) por até `EsperaDescarregarTranscricao`.
- Fora disso, os modelos descarregam sozinhos (Ollama: 10 min; Speaches: 5 min, `WHISPER__TTL`). A 1ª chamada depois
  disso é mais lenta (carga do modelo: ~5–9 s o Whisper, ~4 s um modelo de 4 GB do Ollama).
- O WhisperLiveKit (tempo real) segura ~2 GB enquanto está de pé, com ou sem sessão: carrega o modelo ao subir e não
  solta mais (reiniciar não adianta, ele carrega de novo). Para liberar: `liberar-gpu` (seção 4.9), que para o
  contêiner junto com o descarregar do Ollama e do Speaches; volta com `docker start whisperlivekit`.
- Ver o uso: `nvidia-smi` e `docker exec ollama ollama ps`.

## 9. Problemas comuns

| Sintoma | Causa provável | Solução |
|---|---|---|
| `[FALHA] … não respondeu em http://localhost:…` | Docker Desktop fechado ou contêiner parado | Abra o Docker Desktop; `docker compose up -d` |
| `ModeloNaoInstaladoException` | Modelo não baixado | Rode o comando da mensagem ou `.\scripts\preparar-ambiente.ps1` |
| Chamadas ao Ollama travam por minutos ao trocar de modelo | VM do WSL com pouca memória | `.wslconfig` com `memory=16GB` (12GB com menos de 32 GB de RAM) e `swap=4GB`; `wsl --shutdown` |
| Docker Desktop para de responder ("no route to host") num processamento longo com o WhisperLiveKit no ar | Provável: VM do WSL no limite de memória (não confirmado; aconteceu uma vez com 12 GB) | `memory=16GB` no `.wslconfig`, ou pare o WhisperLiveKit (`docker stop whisperlivekit`) durante o arquivo; reinicie o Docker Desktop |
| Build do WhisperLiveKit falha ou disco cheio | Disco do Docker sem espaço (a imagem tem 22 GB) | Mova o disco do Docker (Settings › Resources › Advanced) |
| Script diz que o contêiner não enxerga a GPU | Driver antigo ou Docker sem backend WSL2 | Atualize o driver (576+); `docker run --rm --gpus all ubuntu nvidia-smi` deve funcionar |
| Porta 8000, 8001 ou 11434 já em uso | Outro programa (ex.: um Ollama instalado no Windows) | Feche o outro programa ou mude a porta no `docker-compose.yml` e no `appsettings.json` |
| Frase repetida várias vezes no JSON, com início = fim | Alucinação do Whisper na transcrição longa | Já tratado com `RepararTrechosSuspeitos=true` (padrão); se sobrar, apague o cache do arquivo e rode de novo |
| Transcrição para de responder depois de descarregar o Whisper à mão | `DELETE /api/ps/...` trava o Speaches 0.8 original | `docker restart speaches`; use a imagem do projeto (`docker compose build speaches`; `docker compose up -d speaches`) |
| Tempo real começa em inglês na fala em português (ou o contrário) | Detecção automática, decidida nos 2 primeiros segundos de fala | `--idioma pt` |
| Tempo real em outro idioma (ex.: espanhol) | WhisperLiveKit sem o arquivo corrigido montado | `docker compose up -d whisperlivekit` (o `docker-compose.yml` monta `docker/whisperlivekit/align_att_base.py`) |
| Arquivo em inglês volta traduzido para o português (ou o contrário) | Detecção errada do Whisper com sotaque | Já tratado com `ConferirIdioma=true` (padrão); se sobrar, `--idioma en` |
| Muitas palavras erradas no microfone | Ruído, música, várias vozes, microfone baixo | `captura` para ver o nível; `--idioma pt`; `--contexto` com nomes |
| Nada aparece no `sistema` | Nada está tocando (o Windows não manda áudio no silêncio) | Toque algo; confira o dispositivo de saída padrão |
| `Não foi possível iniciar a captura` | Microfone ausente ou bloqueado | Windows › Privacidade › Microfone; dispositivo padrão de gravação |
| PowerShell não roda o script | Política de execução | `powershell -ExecutionPolicy Bypass -File .\scripts\preparar-ambiente.ps1` |

## 10. Atualizar

```powershell
git pull
```
```powershell
.\scripts\preparar-ambiente.ps1
```
O script baixa modelos novos que a atualização exigir e reconstrói o que mudou. Resultados em cache feitos com
modelos ou prompts antigos são refeitos automaticamente na próxima vez.
