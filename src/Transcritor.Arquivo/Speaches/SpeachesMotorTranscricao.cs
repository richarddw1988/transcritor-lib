using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Transcritor.Core.Processos;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo.Speaches;

/// <summary>
/// Cliente do Speaches (API compatível com a da OpenAI).
/// Usa POST /v1/audio/transcriptions com verbose_json e timestamp_granularities[] = word e segment.
/// </summary>
public sealed class SpeachesMotorTranscricao(HttpClient http, IOptions<OpcoesArquivo> opcoes)
    : IMotorTranscricao, IServicoVerificavel, IDescarregavel
{
    internal const string Nome = "Speaches";

    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public string NomeServico => Nome;

    public string Identificador => $"speaches:{_opcoes.ModeloTranscricao}";

    private Uri Base => http.BaseAddress ?? _opcoes.SpeachesUrl;

    public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, CancellationToken cancellationToken) =>
        TranscreverAsync(caminhoWav, idioma, null, cancellationToken);

    public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, string? contexto, CancellationToken cancellationToken) =>
        TranscreverAsync(caminhoWav, idioma, contexto, 0, cancellationToken);

    public async Task<TranscricaoBruta> TranscreverAsync(
        string caminhoWav, string? idioma, string? contexto, double temperatura, CancellationToken cancellationToken)
    {
        await using var arquivo = File.OpenRead(caminhoWav);
        using var conteudoArquivo = new StreamContent(arquivo);
        conteudoArquivo.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var formulario = new MultipartFormDataContent
        {
            { conteudoArquivo, "file", Path.GetFileName(caminhoWav) },
            { new StringContent(_opcoes.ModeloTranscricao), "model" },
            { new StringContent("verbose_json"), "response_format" },
            { new StringContent("word"), "timestamp_granularities[]" },
            { new StringContent("segment"), "timestamp_granularities[]" },
            { new StringContent(temperatura.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature" },
        };
        if (!string.IsNullOrWhiteSpace(idioma))
        {
            formulario.Add(new StringContent(Idiomas.Normalizar(idioma)), "language");
        }

        if (!string.IsNullOrWhiteSpace(contexto))
        {
            formulario.Add(new StringContent(contexto.Trim()), "prompt");
        }

        using var resposta = await EnviarAsync(
            () => http.PostAsync(new Uri("v1/audio/transcriptions", UriKind.Relative), formulario, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await GarantirSucessoAsync(resposta, cancellationToken).ConfigureAwait(false);

        var corpo = await resposta.Content.ReadFromJsonAsync<RespostaVerbose>(cancellationToken).ConfigureAwait(false)
            ?? throw new TranscritorException("O Speaches devolveu uma resposta vazia.");
        return Converter(corpo);
    }

    /// <summary>Cabeçalho do /health da imagem do projeto (docker/speaches/Dockerfile), que corrige o DELETE /api/ps.</summary>
    internal const string CabecalhoDescarregar = "X-Transcritor-Descarregar";

    /// <summary>
    /// Tira o Whisper da GPU e volta quando ele saiu. Com a imagem do projeto (o /health traz
    /// <see cref="CabecalhoDescarregar"/>), pede com DELETE /api/ps/{modelo}. Com a imagem original da v0.8 não pede,
    /// porque lá esse comando trava o serviço (a trava do gerenciador de modelos é pega duas vezes): só espera o tempo
    /// ocioso do serviço (<c>WHISPER__TTL</c>), consultando GET /api/ps por no máximo
    /// <see cref="OpcoesArquivo.EsperaDescarregarTranscricao"/>. Falha de rede é ignorada.
    /// </summary>
    public async Task DescarregarAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await AceitaDescarregarAsync(cancellationToken).ConfigureAwait(false))
            {
                var modelo = Uri.EscapeDataString(_opcoes.ModeloTranscricao).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
                using var pedido = ConteinerSobDemanda.MensagemSemLigar(HttpMethod.Delete, new Uri($"api/ps/{modelo}", UriKind.Relative));
                using var resposta = await http.SendAsync(pedido, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException)
        {
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await EsperarSairAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> AceitaDescarregarAsync(CancellationToken cancellationToken)
    {
        using var pedido = ConteinerSobDemanda.MensagemSemLigar(HttpMethod.Get, new Uri("health", UriKind.Relative));
        using var saude = await http.SendAsync(pedido, cancellationToken).ConfigureAwait(false);
        return saude.IsSuccessStatusCode && saude.Headers.Contains(CabecalhoDescarregar);
    }

    /// <summary>Consulta GET /api/ps até o Whisper sumir da lista, no máximo <see cref="OpcoesArquivo.EsperaDescarregarTranscricao"/>.</summary>
    private async Task EsperarSairAsync(CancellationToken cancellationToken)
    {
        var limite = DateTime.UtcNow + _opcoes.EsperaDescarregarTranscricao;
        while (true)
        {
            try
            {
                using var pedido = ConteinerSobDemanda.MensagemSemLigar(HttpMethod.Get, new Uri("api/ps", UriKind.Relative));
                using var resposta = await http.SendAsync(pedido, cancellationToken).ConfigureAwait(false);
                var lista = resposta.IsSuccessStatusCode
                    ? await resposta.Content.ReadFromJsonAsync<ModelosCarregados>(cancellationToken).ConfigureAwait(false)
                    : null;
                if (lista?.Models?.Contains(_opcoes.ModeloTranscricao, StringComparer.OrdinalIgnoreCase) != true)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                return;
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (DateTime.UtcNow >= limite)
            {
                return;
            }

            await Task.Delay(_opcoes.IntervaloConsultaDescarregar, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task VerificarAsync(CancellationToken cancellationToken)
    {
        using (var saude = await EnviarAsync(() => http.GetAsync(new Uri("health", UriKind.Relative), cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            await GarantirSucessoAsync(saude, cancellationToken).ConfigureAwait(false);
        }

        using var modelos = await EnviarAsync(() => http.GetAsync(new Uri("v1/models", UriKind.Relative), cancellationToken), cancellationToken).ConfigureAwait(false);
        await GarantirSucessoAsync(modelos, cancellationToken).ConfigureAwait(false);
        var lista = await modelos.Content.ReadFromJsonAsync<ListaModelos>(cancellationToken).ConfigureAwait(false);
        if (lista?.Data?.Any(m => string.Equals(m.Id, _opcoes.ModeloTranscricao, StringComparison.OrdinalIgnoreCase)) != true)
        {
            throw ModeloAusente();
        }
    }

    internal static TranscricaoBruta Converter(RespostaVerbose corpo)
    {
        var segmentos = (corpo.Segments ?? [])
            .Select(s => new SegmentoBruto(s.Start, s.End, s.Text ?? string.Empty, s.NoSpeechProb))
            .ToList();
        var palavras = (corpo.Words ?? [])
            .Select(w => new Palavra(w.Word ?? string.Empty, w.Start, w.End, w.Probability is { } p ? Math.Round(p, 3) : null))
            .ToList();

        var idioma = string.IsNullOrWhiteSpace(corpo.Language) ? "und" : Idiomas.Normalizar(corpo.Language);
        var duracao = corpo.Duration ?? segmentos.LastOrDefault()?.Fim ?? 0;
        return new TranscricaoBruta(idioma, duracao, segmentos, palavras);
    }

    private async Task<HttpResponseMessage> EnviarAsync(Func<Task<HttpResponseMessage>> enviar, CancellationToken cancellationToken)
    {
        try
        {
            return await enviar().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServicoIndisponivelException(Nome, Base, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServicoIndisponivelException(
                $"O {Nome} em {Base} não respondeu dentro do tempo limite ({http.Timeout}). Para arquivos longos, aumente TimeoutTranscricao.",
                ex);
        }
    }

    private async Task GarantirSucessoAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        if (resposta.IsSuccessStatusCode)
        {
            return;
        }

        var corpo = await resposta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (resposta.StatusCode == HttpStatusCode.NotFound && corpo.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            throw ModeloAusente();
        }

        if (resposta.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            throw new ServicoIndisponivelException(Nome, Base, new HttpRequestException(corpo, null, resposta.StatusCode));
        }

        var resumo = corpo.Length <= 500 ? corpo : corpo[..500] + "...";
        throw new TranscritorException(string.Create(
            CultureInfo.InvariantCulture,
            $"O {Nome} respondeu {(int)resposta.StatusCode} ({resposta.StatusCode}): {resumo}"));
    }

    private ModeloNaoInstaladoException ModeloAusente() =>
        new(Nome, _opcoes.ModeloTranscricao, $"curl.exe -X POST {new Uri(Base, "v1/models/" + _opcoes.ModeloTranscricao)}");

    internal sealed record RespostaVerbose(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("segments")] IReadOnlyList<SegmentoVerbose>? Segments,
        [property: JsonPropertyName("words")] IReadOnlyList<PalavraVerbose>? Words);

    internal sealed record SegmentoVerbose(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("no_speech_prob")] double? NoSpeechProb = null);

    internal sealed record PalavraVerbose(
        [property: JsonPropertyName("word")] string? Word,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("probability")] double? Probability = null);

    internal sealed record ListaModelos([property: JsonPropertyName("data")] IReadOnlyList<ModeloListado>? Data);

    internal sealed record ModeloListado([property: JsonPropertyName("id")] string Id);

    private sealed record ModelosCarregados([property: JsonPropertyName("models")] IReadOnlyList<string>? Models);
}
