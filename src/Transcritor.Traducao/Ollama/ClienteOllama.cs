using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Transcritor.Core.Erros;

namespace Transcritor.Traducao.Ollama;

internal sealed record MensagemChat(
    [property: JsonPropertyName("role")] string Papel,
    [property: JsonPropertyName("content")] string Conteudo);

/// <summary>Chamadas ao Ollama: POST /api/chat (sem streaming), GET /api/version e /api/tags.</summary>
internal sealed class ClienteOllama(HttpClient http, IOptions<OpcoesTraducao> opcoes)
{
    internal const string Nome = "Ollama";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly OpcoesTraducao _opcoes = opcoes.Value;

    private Uri Base => http.BaseAddress ?? _opcoes.OllamaUrl;

    /// <summary>Devolve message.content da resposta.</summary>
    /// <param name="modelo">Modelo do Ollama.</param>
    /// <param name="mensagens">Conversa.</param>
    /// <param name="formato">JSON Schema da resposta, se houver.</param>
    /// <param name="maxTokens">Teto de tokens gerados (num_predict).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    internal async Task<string> ConversarAsync(
        string modelo,
        IReadOnlyList<MensagemChat> mensagens,
        JsonElement? formato,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var pedido = new PedidoChat(
            modelo,
            mensagens,
            Stream: false,
            Format: formato,
            KeepAlive: _opcoes.KeepAlive,
            Options: new OpcoesModelo(Temperature: 0, NumPredict: maxTokens));

        using var resposta = await EnviarAsync(
            () => http.PostAsJsonAsync(new Uri("api/chat", UriKind.Relative), pedido, Json, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await GarantirSucessoAsync(resposta, modelo, cancellationToken).ConfigureAwait(false);

        var corpo = await resposta.Content.ReadFromJsonAsync<RespostaChat>(Json, cancellationToken).ConfigureAwait(false);
        return corpo?.Message?.Content
            ?? throw new TranscritorException($"O {Nome} devolveu uma resposta sem message.content.");
    }

    /// <summary>Confere se o Ollama responde e se os modelos estão instalados.</summary>
    internal async Task VerificarAsync(IEnumerable<string> modelos, CancellationToken cancellationToken)
    {
        using (var versao = await EnviarAsync(() => http.GetAsync(new Uri("api/version", UriKind.Relative), cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            await GarantirSucessoAsync(versao, null, cancellationToken).ConfigureAwait(false);
        }

        using var tags = await EnviarAsync(() => http.GetAsync(new Uri("api/tags", UriKind.Relative), cancellationToken), cancellationToken).ConfigureAwait(false);
        await GarantirSucessoAsync(tags, null, cancellationToken).ConfigureAwait(false);
        var lista = await tags.Content.ReadFromJsonAsync<ListaTags>(Json, cancellationToken).ConfigureAwait(false);
        var instalados = (lista?.Models ?? []).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var modelo in modelos.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!instalados.Contains(modelo) && !instalados.Contains(modelo + ":latest"))
            {
                throw ModeloAusente(modelo);
            }
        }
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
                $"O {Nome} em {Base} não respondeu dentro do tempo limite ({http.Timeout}).", ex);
        }
    }

    private async Task GarantirSucessoAsync(HttpResponseMessage resposta, string? modelo, CancellationToken cancellationToken)
    {
        if (resposta.IsSuccessStatusCode)
        {
            return;
        }

        var corpo = await resposta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (modelo is not null && resposta.StatusCode == HttpStatusCode.NotFound && corpo.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw ModeloAusente(modelo);
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

    private static ModeloNaoInstaladoException ModeloAusente(string modelo) =>
        new(Nome, modelo, $"docker exec ollama ollama pull {modelo}");

    private sealed record PedidoChat(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<MensagemChat> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] JsonElement? Format,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("options")] OpcoesModelo Options);

    private sealed record OpcoesModelo(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record RespostaChat([property: JsonPropertyName("message")] MensagemResposta? Message);

    private sealed record MensagemResposta([property: JsonPropertyName("content")] string? Content);

    private sealed record ListaTags([property: JsonPropertyName("models")] IReadOnlyList<ModeloTag>? Models);

    private sealed record ModeloTag([property: JsonPropertyName("name")] string Name);
}
