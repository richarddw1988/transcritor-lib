using System.Runtime.ExceptionServices;

namespace Transcritor.Core.Processos;

/// <summary>
/// Etapa do HttpClient de um serviço em contêiner: antes de cada chamada pega um uso no
/// <see cref="GerenciadorConteineres"/> (que liga o contêiner parado e espera <paramref name="rotaPronto"/> responder) e o
/// solta quando a resposta chega. Chamadas marcadas com <see cref="SemLigar"/> (tirar modelo da GPU, por exemplo) não
/// ligam o contêiner: parado, falham como serviço fora do ar.
/// </summary>
/// <param name="gerenciador">Gerenciador do programa (singleton).</param>
/// <param name="conteiner">Nome do contêiner; vazio não gerencia.</param>
/// <param name="rotaPronto">Rota, a partir da raiz do serviço, que responde 2xx quando ele está pronto (ex.: "health").</param>
public sealed class ConteinerSobDemanda(GerenciadorConteineres gerenciador, string conteiner, string rotaPronto) : DelegatingHandler
{
    /// <summary>Marca a chamada que não deve ligar o contêiner.</summary>
    public static readonly HttpRequestOptionsKey<bool> SemLigar = new("Transcritor.SemLigar");

    /// <summary>Mensagem já marcada com <see cref="SemLigar"/>.</summary>
    public static HttpRequestMessage MensagemSemLigar(HttpMethod metodo, Uri uri, HttpContent? conteudo = null)
    {
        var mensagem = new HttpRequestMessage(metodo, uri) { Content = conteudo };
        mensagem.Options.Set(SemLigar, true);
        return mensagem;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.TryGetValue(SemLigar, out var semLigar) && semLigar)
        {
            if (await gerenciador.NoArAsync(conteiner, cancellationToken).ConfigureAwait(false) == false)
            {
                throw new HttpRequestException($"O contêiner {conteiner} está parado.");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var raiz = new Uri(request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/");
        Task<bool> ProntoAsync(CancellationToken ct) => ResponderAsync(new Uri(raiz, rotaPronto), ct);

        await using var uso = (await gerenciador.UsarAsync(conteiner, ProntoAsync, null, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        ExceptionDispatchInfo falha;
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            falha = ExceptionDispatchInfo.Capture(ex);
        }

        // Alguém parou o contêiner fora da lib: liga de novo e tenta mais uma vez.
        if (await gerenciador.ReligarSeParadoAsync(conteiner, ProntoAsync, cancellationToken).ConfigureAwait(false))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        falha.Throw();
        return null!;
    }

    private async Task<bool> ResponderAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, uri);
        using var tempo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tempo.CancelAfter(TimeSpan.FromSeconds(5));
        using var resposta = await base.SendAsync(pedido, tempo.Token).ConfigureAwait(false);
        return resposta.IsSuccessStatusCode;
    }
}
