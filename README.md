# transcritor-lib

Lib .NET 10 de transcrição e tradução de fala, tudo local. Os modelos rodam em Docker
(Speaches, WhisperLiveKit, Ollama); a lib só faz as chamadas. Detalhes da verificação
inicial em [docs/etapa-0.md](docs/etapa-0.md).

## Projetos

| Projeto | Papel |
|---|---|
| `Transcritor.Core` | Contratos (`ITranscritorArquivo`, `IMotorTranscricao`, ...), modelos do JSON e erros. Sem dependências. |
| `Transcritor.Arquivo` | ffprobe/ffmpeg → WAV 16 kHz mono → Speaches → frases e palavras → JSON, com cache pelo SHA-256. |
| `Transcritor.Cli` | App de console para testar (`transcritor arquivo`, `transcritor servicos`). |
| `Transcritor.Tests` | Testes unitários com fakes de HTTP e de processo. |

## Uso

```csharp
services.AddTranscritorArquivo(o => o.SpeachesUrl = new Uri("http://localhost:8000"));
var r = await sp.GetRequiredService<ITranscritorArquivo>().ProcessarAsync("aula.mp4", progresso, ct);
```

O JSON vai para o cache (`%LOCALAPPDATA%\Transcritor\cache`) e para `aula.mp4.transcricao.json`.

## Testar

```powershell
docker compose up -d speaches
dotnet test
dotnet run --project src/Transcritor.Cli -- servicos
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4
```

Rodar `arquivo` de novo no mesmo arquivo responde do cache, sem chamar ffmpeg nem Speaches.
Configuração em `src/Transcritor.Cli/appsettings.json` ou variáveis `TRANSCRITOR_Transcritor__Arquivo__SpeachesUrl` etc.
