using System.Net;
using Transcritor.Arquivo.Speaches;
using Transcritor.Core.Erros;

namespace Transcritor.Tests;

public sealed class SpeachesMotorTranscricaoTests : IDisposable
{
    private const string RespostaVerbose = """
        {
          "task": "transcribe", "language": "en", "duration": 4.2, "text": "The mind behind Linux",
          "segments": [ { "id": 0, "seek": 0, "start": 0.0, "end": 2.0, "text": " The mind behind Linux",
                          "tokens": [1], "temperature": 0.0, "avg_logprob": -0.1, "compression_ratio": 1.0, "no_speech_prob": 0.01 } ],
          "words": [ { "word": " The", "start": 0.0, "end": 0.2 }, { "word": " mind", "start": 0.2, "end": 0.6 } ]
        }
        """;

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static SpeachesMotorTranscricao Criar(FakeHttpHandler handler, string? modelo = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://speaches.test:8000/") };
        return new SpeachesMotorTranscricao(http, Opcoes.Arquivo(o =>
        {
            o.SpeachesUrl = new Uri("http://speaches.test:8000");
            if (modelo is not null) o.ModeloTranscricao = modelo;
        }));
    }

    [Fact]
    public async Task Envia_campos_de_tempo_por_palavra_e_interpreta_resposta()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(RespostaVerbose));
        var motor = Criar(handler, "Systran/faster-whisper-large-v3");
        var wav = _pasta.Arquivo("a.wav", "RIFF");

        var bruta = await motor.TranscreverAsync(wav, "pt-BR", CancellationToken.None);

        var (requisicao, corpo) = Assert.Single(handler.Recebidas);
        Assert.Equal(HttpMethod.Post, requisicao.Method);
        Assert.Equal("http://speaches.test:8000/v1/audio/transcriptions", requisicao.RequestUri!.ToString());
        Assert.Contains("name=model", corpo, StringComparison.Ordinal);
        Assert.Contains("Systran/faster-whisper-large-v3", corpo, StringComparison.Ordinal);
        Assert.Contains("verbose_json", corpo, StringComparison.Ordinal);
        Assert.Contains("name=\"timestamp_granularities[]\"", corpo, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\nword\r\n", corpo, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\nsegment\r\n", corpo, StringComparison.Ordinal);
        Assert.Contains("name=language", corpo, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\npt\r\n", corpo, StringComparison.Ordinal);

        Assert.Equal("en", bruta.Idioma);
        Assert.Equal(4.2, bruta.Duracao);
        Assert.Single(bruta.Segmentos);
        Assert.Equal(2, bruta.Palavras.Count);
        Assert.Equal(" mind", bruta.Palavras[1].Texto);
    }

    [Fact]
    public async Task Sem_idioma_nao_envia_language()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(RespostaVerbose));
        await Criar(handler).TranscreverAsync(_pasta.Arquivo("a.wav", "RIFF"), null, CancellationToken.None);

        Assert.DoesNotContain("name=language", handler.Recebidas[0].Corpo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servico_fora_do_ar_gera_erro_dizendo_qual()
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<ServicoIndisponivelException>(
            () => Criar(handler).TranscreverAsync(_pasta.Arquivo("a.wav", "RIFF"), null, CancellationToken.None));

        Assert.Equal("Speaches", ex.Servico);
        Assert.Contains("Speaches", ex.Message, StringComparison.Ordinal);
        Assert.Contains("speaches.test:8000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modelo_nao_instalado_gera_erro_com_comando_para_instalar()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(
            """{"detail":"Model 'x/y' is not installed locally. You can download the model using `POST /v1/models`"}""",
            HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<ModeloNaoInstaladoException>(
            () => Criar(handler, "x/y").TranscreverAsync(_pasta.Arquivo("a.wav", "RIFF"), null, CancellationToken.None));

        Assert.Equal("x/y", ex.Modelo);
        Assert.Contains("/v1/models/x/y", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outro_erro_http_mostra_status_e_corpo()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"detail":"boom"}""", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<TranscritorException>(
            () => Criar(handler).TranscreverAsync(_pasta.Arquivo("a.wav", "RIFF"), null, CancellationToken.None));

        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verificar_acusa_modelo_ausente_na_lista()
    {
        var handler = new FakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/health" => FakeHttpHandler.Json("OK"),
            "/v1/models" => FakeHttpHandler.Json("""{"object":"list","data":[{"id":"Systran/faster-whisper-small"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        await Assert.ThrowsAsync<ModeloNaoInstaladoException>(
            () => Criar(handler, "Systran/faster-whisper-large-v3").VerificarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Verificar_passa_com_modelo_instalado()
    {
        var handler = new FakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath == "/health"
            ? FakeHttpHandler.Json("OK")
            : FakeHttpHandler.Json("""{"data":[{"id":"Systran/faster-whisper-large-v3"}]}"""));

        await Criar(handler, "Systran/faster-whisper-large-v3").VerificarAsync(CancellationToken.None);
    }
}
