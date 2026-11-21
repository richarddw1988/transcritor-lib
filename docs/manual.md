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
| Dicionário local (opcional) | Sentidos reais de uma palavra ou expressão, inglês ↔ português, com a forma base das flexões ("gave" → "give") | SQLite gerado do Wiktionary |

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
     **50 GB livres** ou mais (a 1ª preparação baixa ~48 GB).
3. **Memória da VM do WSL**: se existir `%USERPROFILE%\.wslconfig` com `memory` abaixo de 10 GB, aumente (abaixo,
   12 GB + 4 GB de swap) e rode `wsl --shutdown`; com 4 GB o Ollama trava ao trocar de modelo.
   ```ini
   [wsl2]
   memory=12GB
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
2. constrói a imagem do WhisperLiveKit (não há imagem publicada; ~22 GB, 20+ min na 1ª vez) e sobe os serviços;
3. espera cada serviço responder e confere se os contêineres enxergam a GPU;
4. baixa os modelos que faltam (Whisper no Speaches; translategemma 12b e 4b e gemma3 4b no Ollama);
5. compila a solução e roda `transcritor servicos`.

No fim deve aparecer **Ambiente pronto**. Na 1ª vez leva de 20 a 60 min, conforme a internet.

Opções do script:

| Opção | Efeito |
|---|---|
| `-SoConferir` | Só confere; não constrói, não sobe, não baixa |
| `-SemTempoReal` | Não constrói nem sobe o WhisperLiveKit (economiza ~22 GB); arquivo e tradução continuam |
| `-ComAnaliseDeSentido` | Também baixa o `gemma3:12b` (análise de sentido, ~8 GB) |
| `-ModeloTranscricao`, `-ModeloFrases`, `-ModeloFrasesTempoReal`, `-ModeloPalavras`, `-ModeloAnalise` | Usa outros modelos (ponha os mesmos no `appsettings.json` do seu app) |
| `-ComDicionario` | Também baixa o Wiktionary (~600 MB, kaikki.org) para `dados/` e gera `dados/dicionario.db` (~60 MB, ~30 s). Ver 4.6 |

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

### 4.2 Transcrever (e traduzir) um arquivo
```powershell
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos
```
- Grava `C:\videos\aula.mp4.transcricao.json` e guarda no cache (repetir responde em ~0,1 s, sem chamar serviços).
- Mostra idioma, duração, quantidade de frases e palavras, e as 5 primeiras frases com tradução.
- `--idioma pt` força o idioma (evita erro de detecção); `--json saida.json` grava uma cópia; `--modelo` troca o Whisper.
- `--sentido` também gera os blocos (expressões) e o "o que quis dizer" de cada frase. É lento (~10–13 s por frase)
  e precisa do `gemma3:12b` (`preparar-ambiente.ps1 -ComAnaliseDeSentido`); com 12 GB de GPU, pare o tempo real antes
  (`docker stop whisperlivekit`).

Tempo de referência (RTX 3060): 6 min de áudio em inglês → ~8 min com a tradução 12b (7 min só da tradução);
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

### 4.6 Dicionário inglês ↔ português (opcional)
Dicionário local gerado do Wiktionary (extração do kaikki.org, licença CC BY-SA 4.0 / GFDL). Não usa GPU nem
serviços. A pasta `dados/` não vai para o Git.

1. Baixe os dois dumps (~600 MB) e gere o SQLite — ou rode `.\scripts\preparar-ambiente.ps1 -ComDicionario`, que faz isso:
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
   dotnet run --project src/Transcritor.Cli -- dicionario importar
   ```
   `--ingles`, `--portugues` e `--banco` trocam os caminhos (padrões: `dados\kaikki-en.jsonl.gz`,
   `dados\kaikki-pt.jsonl.gz` e `dados\dicionario.db`).
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

Dicionário gerado por uma versão anterior da lib é recusado com a mensagem de gerar de novo: rode
`transcritor dicionario importar` (os dumps baixados continuam valendo).

O que tem e quanto custa: [modelos.md](modelos.md#dicionário-local-wiktionary).

Com o dicionário gerado, a tradução de palavra (comando `palavra` e `TraduzirPalavraAsync`) passa a usá-lo, pela
opção `CaminhoDicionario` (seção 6), que no Cli já aponta para `dados/dicionario.db`:
- sem a tradução da frase (`--traducao-frase`), o modelo recebe os sentidos da palavra e das expressões que a contêm
  como opções numeradas, marcando os que aparecem na tradução que a lib fez da frase, e escolhe um;
- com a tradução da frase, só quando a palavra faz parte de uma expressão do dicionário ("gave" em "gave up",
  "running" em "running out of"); nos outros casos a tradução da frase já é a melhor pista.

### 4.7 Medir a tradução de palavra
```powershell
dotnet run --project src/Transcritor.Cli -- avaliar-palavras docs/avaliacao/palavras-ambiguas.json
```
Traduz cada caso do arquivo (palavra, frase, tradução da frase, idiomas e respostas aceitas), sem cache, e mostra
acertos e tempo. `--sem-dicionario` desliga o dicionário; `--sem-traducao-frase` não passa a tradução da frase.
Uma resposta conta como certa se contém uma das `aceitas`.

### 4.8 Medir o reconhecimento da sua fala
1. Grave as frases de `docs/avaliacao/frases-para-gravar.json` pelo microfone (mostra cada frase, Enter grava; salva
   em `dados/gravacoes/<id>.wav`, fora do Git; frases já gravadas são puladas, `-Regravar` refaz):
   ```powershell
   .\scripts\gravar-frases.ps1
   ```
2. Meça:
   ```powershell
   dotnet run --project src/Transcritor.Cli -- avaliar-fala
   ```
   Transcreve cada gravação no idioma da frase, alinha com o texto certo e mostra cada troca, falta e sobra de
   palavra, com a confiança da palavra ouvida e em que posição a palavra certa aparece entre as 15 parecidas do
   dicionário (`dicionario parecidas`). No fim: WER (taxa de erro por palavra), quantas palavras erradas ficaram abaixo
   de `--limite-confianca` (padrão 0,5) e quantas certas também ficaram (alarmes falsos). `--frases`, `--pasta` e
   `--banco` trocam os caminhos.
   - O Whisper escreve números como dígitos ("sete" → "7"), o que conta como troca: evite números nas frases.

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
- Frase = sentença completa (não corta em "Mrs." nem no meio da sentença); `traducao` só quando a saída pede.
- `sentido` e `blocos` só com `AnalisarSentido = true`; `palavraInicial/palavraFinal` são índices em `palavras`.
- `confianca` (0 a 1) é a probabilidade que o Whisper deu à palavra; baixa indica provável erro de reconhecimento.
  Não aparece quando o motor não informa, nem em JSON gravado antes deste campo existir (`Palavra.Confianca` nulo).
- Formato detalhado: [etapa-2.md](etapa-2.md) (regra da saída) e [etapa-4.md](etapa-4.md) (blocos).

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
- Para gerar pelo código: `ImportadorWiktionary.ImportarAsync(jsonlIngles, jsonlPortugues, caminhoBanco)`.
- Palavras de som parecido: `dicionario.ProcurarParecidas("weather", "en")` devolve `Parecida(Termo, DistanciaPronuncia,
  DistanciaEscrita, Sentidos)` — ex.: `feather`, `whether`, `water`…; a própria palavra não entra.

### 5.5 Erros
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
| `CaminhoFfmpeg` / `CaminhoFfprobe` | `ffmpeg` / `ffprobe` | Executáveis (PATH ou caminho completo) |
| `PastaCache` | `%LOCALAPPDATA%\Transcritor\cache` | Cache dos resultados |
| `GravarJsonAoLado` | `true` | Grava `<arquivo>.transcricao.json` ao lado da mídia |
| `TimeoutTranscricao` | `01:00:00` | Tempo máximo de uma transcrição |
| `RepararTrechosSuspeitos` | `true` | Transcreve de novo, isolado, cada trecho em que o Whisper se perdeu na transcrição longa — 3+ palavras com duração zero (alucinação/repetição) ou 15 s+ sem nenhuma pontuação — e troca se o resultado for melhor |
| `MaximoTrechosReparados` | `30` | Limite de trechos refeitos por arquivo (cada um é uma chamada a mais ao Speaches) |

**`Transcritor:Traducao`**

| Chave | Padrão | O que é |
|---|---|---|
| `OllamaUrl` | `http://localhost:11434` | Ollama |
| `ModeloFrases` | `translategemma:12b` | Frases do arquivo |
| `ModeloFrasesTempoReal` | `translategemma:4b` | Frases no tempo real e comando `traduzir` (vazio = `ModeloFrases`) |
| `ModeloPalavras` | `gemma3:4b` | Palavra solta |
| `CaminhoDicionario` | nulo (no Cli: `dados/dicionario.db`) | Dicionário da seção 4.6. Com ele, a palavra solta recebe os sentidos do dicionário como opções numeradas e o modelo escolhe uma; nulo ou arquivo ausente = só o modelo |
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
`docker compose up -d whisperlivekit`.

## 7. Cache

- Resultados de arquivo: um JSON por chave em `%LOCALAPPDATA%\Transcritor\cache`. A chave junta o hash do arquivo, o
  modelo e as versões da montagem, do reparo de trechos, da tradução e da análise; trocar modelo ou prompt gera
  resultado novo.
- Palavras: `palavras.json` na mesma pasta.
- Para limpar: apague a pasta (os serviços refazem tudo na próxima vez).

## 8. GPU e memória

- Os três serviços dividem a GPU. Com 12 GB: tempo real + tradução 4b cabem juntos; a análise de sentido (12b) pede
  o tempo real parado (`docker stop whisperlivekit`; volta com `docker start whisperlivekit`).
- Modelos descarregam sozinhos (Ollama: 10 min; Speaches: 5 min). A 1ª chamada depois disso é lenta (carga do modelo).
- Ver o uso: `nvidia-smi` e `docker exec ollama ollama ps`.

## 9. Problemas comuns

| Sintoma | Causa provável | Solução |
|---|---|---|
| `[FALHA] … não respondeu em http://localhost:…` | Docker Desktop fechado ou contêiner parado | Abra o Docker Desktop; `docker compose up -d` |
| `ModeloNaoInstaladoException` | Modelo não baixado | Rode o comando da mensagem ou `.\scripts\preparar-ambiente.ps1` |
| Chamadas ao Ollama travam por minutos ao trocar de modelo | VM do WSL com pouca memória | `.wslconfig` com `memory=12GB` e `swap=4GB`; `wsl --shutdown` |
| Build do WhisperLiveKit falha ou disco cheio | Disco do Docker sem espaço (a imagem tem 22 GB) | Mova o disco do Docker (Settings › Resources › Advanced) |
| Script diz que o contêiner não enxerga a GPU | Driver antigo ou Docker sem backend WSL2 | Atualize o driver (576+); `docker run --rm --gpus all ubuntu nvidia-smi` deve funcionar |
| Porta 8000, 8001 ou 11434 já em uso | Outro programa (ex.: um Ollama instalado no Windows) | Feche o outro programa ou mude a porta no `docker-compose.yml` e no `appsettings.json` |
| Frase repetida várias vezes no JSON, com início = fim | Alucinação do Whisper na transcrição longa | Já tratado com `RepararTrechosSuspeitos=true` (padrão); se sobrar, apague o cache do arquivo e rode de novo |
| Tempo real começa em inglês na fala em português | Detecção automática puxa para o inglês | `--idioma pt` |
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
