# transcritor-lib

Lib .NET 10 de transcrição e tradução de fala, tudo local. Os modelos rodam em Docker
(Speaches, WhisperLiveKit, Ollama); a lib só faz as chamadas. Detalhes da verificação
inicial em [docs/etapa-0.md](docs/etapa-0.md).

## Projetos

| Projeto | Papel |
|---|---|
| `Transcritor.Core` | Contratos (`ITranscritorArquivo`, `IMotorTranscricao`, ...), modelos do JSON e erros. Sem dependências. |
| `Transcritor.Arquivo` | ffprobe/ffmpeg → WAV 16 kHz mono → Speaches → frases e palavras → JSON, com cache pelo SHA-256. |
| `Transcritor.Traducao` | Tradução pt ↔ en pelo sentido via Ollama: frases com as vizinhas como contexto (translategemma 12b no arquivo, 4b no tempo real), palavra solta com a frase (gemma3), cache de palavras. |
| `Transcritor.TempoReal` | Cliente WebSocket do WhisperLiveKit: eventos `Provisorio`/`Confirmado` (`IAsyncEnumerable`), tradução dos trechos confirmados, `FonteAudioWav` para testes. |
| `Transcritor.Captura` | Windows (WASAPI/NAudio): microfone e som do sistema em PCM 16 kHz mono. |
| `Transcritor.Cli` | App de console para testar (`arquivo`, `traduzir`, `palavra`, `microfone`, `sistema`, `simular`, `captura`, `servicos`). |
| `Transcritor.Tests` | Testes unitários com fakes de HTTP e de processo. |

## Uso

```csharp
services.AddTranscritorArquivo(o => o.SpeachesUrl = new Uri("http://localhost:8000"));
services.AddTranscritorTraducao(o => o.OllamaUrl = new Uri("http://localhost:11434"));
services.AddTranscritorTempoReal(o => o.WhisperLiveKitUrl = new Uri("ws://localhost:8001/asr"));
services.AddTranscritorCaptura(); // Windows

var r = await sp.GetRequiredService<ITranscritorArquivo>().ProcessarAsync("aula.mp4", Saida.Ambos, progresso, ct);
var p = await sp.GetRequiredService<ITradutor>().TraduzirPalavraAsync(
    new PedidoTraducaoPalavra("banco", frase.Texto, "pt", "en", frase.Traducao), ct);

await foreach (var e in sp.GetRequiredService<ITranscritorTempoReal>().IniciarAsync(Fonte.Microfone, Saida.Ambos, cancellationToken: parar))
{
    // Provisorio: texto que ainda pode mudar. Confirmado: trecho final; chega de novo, com o mesmo Id, quando a tradução fica pronta.
}
```

O JSON vai para o cache (`%LOCALAPPDATA%\Transcritor\cache`) e para `aula.mp4.transcricao.json`.
`texto` fica no idioma falado; `traducao` vem quando a `Saida` pede o outro idioma (regra em [docs/etapa-2.md](docs/etapa-2.md)).

## Preparar o ambiente

Pré-requisitos: Windows 10/11, GPU NVIDIA, [Docker Desktop](https://www.docker.com/products/docker-desktop/) (WSL2),
.NET SDK 10 e ffmpeg (`winget install Microsoft.DotNet.SDK.10` e `winget install Gyan.FFmpeg`).

Um script faz o resto: confere os pré-requisitos, constrói a imagem do WhisperLiveKit (não há imagem
publicada; o `docker-compose.yml` a constrói a partir da tag `v0.2.27`), sobe os serviços, baixa os modelos
que faltam e roda `transcritor servicos`. Pode rodar quantas vezes quiser; só faz o que falta.

```powershell
.\scripts\preparar-ambiente.ps1
```

- Na 1ª vez baixa ~48 GB (imagem do WhisperLiveKit 22 GB, Speaches 8 GB, Ollama, modelos) e leva de 20 a 60 min.
- `-ComAnaliseDeSentido` também baixa o modelo da análise de sentido (`gemma3:12b`, ~8 GB).
- `-SoConferir` só confere, sem baixar nada; `-SemTempoReal` dispensa o WhisperLiveKit (arquivo e tradução continuam).
- Outros modelos: `-ModeloTranscricao`, `-ModeloFrases`, `-ModeloFrasesTempoReal`, `-ModeloPalavras` (ponha os mesmos no `appsettings.json`).
- Se o PowerShell bloquear o script: `powershell -ExecutionPolicy Bypass -File .\scripts\preparar-ambiente.ps1`.
- O script avisa se o disco do Docker tiver pouco espaço ou se o `.wslconfig` limitar a VM a menos de 10 GB
  (com 4 GB o Ollama trava ao trocar de modelo).

| Serviço | Porta | Imagem | Modelos |
|---|---|---|---|
| Speaches (arquivo) | 8000 | `ghcr.io/speaches-ai/speaches:latest-cuda-12.6.3` | `Systran/faster-whisper-large-v3` |
| WhisperLiveKit (tempo real) | 8001 | construída localmente | `medium` (baixado na 1ª subida) |
| Ollama (tradução) | 11434 | `ollama/ollama` | `translategemma:12b` (frases do arquivo), `translategemma:4b` (tempo real), `gemma3:4b` (palavra); opcional `gemma3:12b` (análise de sentido) |

## Testar

```powershell
dotnet test
dotnet run --project src/Transcritor.Cli -- servicos
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos --sentido
dotnet run --project src/Transcritor.Cli -- traduzir "O banco fecha às quatro." --de pt --para en --antes "Preciso pagar contas."
dotnet run --project src/Transcritor.Cli -- palavra banco --frase "O banco foi pintado de azul." --de pt --para en
dotnet run --project src/Transcritor.Cli -- captura --fonte Microfone --segundos 5
dotnet run --project src/Transcritor.Cli -- microfone --saida Ambos --idioma pt
dotnet run --project src/Transcritor.Cli -- sistema --saida Ambos
dotnet run --project src/Transcritor.Cli -- simular C:\videos\aula.mp4 --saida Ambos
```

Tempo real: medidas e limites em [docs/etapa-3.md](docs/etapa-3.md).
Blocos de sentido (expressões) e "o que quis dizer" no JSON: [docs/etapa-4.md](docs/etapa-4.md).

Rodar `arquivo` de novo no mesmo arquivo responde do cache, sem chamar ffmpeg nem Speaches.
Configuração em `src/Transcritor.Cli/appsettings.json` ou variáveis `TRANSCRITOR_Transcritor__Arquivo__SpeachesUrl` etc.
