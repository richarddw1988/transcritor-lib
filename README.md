# transcritor-lib

Lib .NET 10 de transcrição e tradução de fala, tudo local. Os modelos rodam em Docker
(Speaches, WhisperLiveKit, Ollama); a lib só faz as chamadas. Detalhes da verificação
inicial em [docs/etapa-0.md](docs/etapa-0.md).

## Projetos

| Projeto | Papel |
|---|---|
| `Transcritor.Core` | Contratos (`ITranscritorArquivo`, `IMotorTranscricao`, ...), modelos do JSON e erros. Sem dependências. |
| `Transcritor.Arquivo` | ffprobe/ffmpeg → WAV 16 kHz mono → Speaches → frases e palavras → JSON, com cache pelo SHA-256. |
| `Transcritor.Traducao` | Tradução pt ↔ en pelo sentido via Ollama: frases com as vizinhas como contexto (translategemma), palavra solta com a frase (gemma3), cache de palavras. |
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

## Testar

```powershell
docker compose up -d --build
curl.exe -X POST http://localhost:8000/v1/models/Systran/faster-whisper-large-v3
docker exec ollama ollama pull translategemma:4b
docker exec ollama ollama pull gemma3:4b
dotnet test
dotnet run --project src/Transcritor.Cli -- servicos
dotnet run --project src/Transcritor.Cli -- arquivo C:\videos\aula.mp4 --saida Ambos
dotnet run --project src/Transcritor.Cli -- traduzir "O banco fecha às quatro." --de pt --para en --antes "Preciso pagar contas."
dotnet run --project src/Transcritor.Cli -- palavra banco --frase "O banco foi pintado de azul." --de pt --para en
dotnet run --project src/Transcritor.Cli -- captura --fonte Microfone --segundos 5
dotnet run --project src/Transcritor.Cli -- microfone --saida Ambos --idioma pt
dotnet run --project src/Transcritor.Cli -- sistema --saida Ambos
dotnet run --project src/Transcritor.Cli -- simular C:ideosula.mp4 --saida Ambos
```

Tempo real: medidas e limites em [docs/etapa-3.md](docs/etapa-3.md).

Rodar `arquivo` de novo no mesmo arquivo responde do cache, sem chamar ffmpeg nem Speaches.
Configuração em `src/Transcritor.Cli/appsettings.json` ou variáveis `TRANSCRITOR_Transcritor__Arquivo__SpeachesUrl` etc.
