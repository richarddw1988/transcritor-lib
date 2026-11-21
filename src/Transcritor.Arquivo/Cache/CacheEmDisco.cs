using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo.Cache;

/// <summary>Um JSON por chave na pasta <see cref="OpcoesArquivo.PastaCache"/>.</summary>
public sealed class CacheEmDisco(IOptions<OpcoesArquivo> opcoes) : ICacheResultado
{
    private readonly string _pasta = opcoes.Value.PastaCache;

    public async Task<ResultadoTranscricao?> LerAsync(string chave, CancellationToken cancellationToken)
    {
        var caminho = Caminho(chave);
        if (!File.Exists(caminho))
        {
            return null;
        }

        try
        {
            return await JsonTranscricao.LerAsync(caminho, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Arquivo de cache corrompido: trata como ausente e reprocessa.
            return null;
        }
    }

    public Task GravarAsync(string chave, ResultadoTranscricao resultado, CancellationToken cancellationToken) =>
        JsonTranscricao.GravarAsync(resultado, Caminho(chave), cancellationToken);

    private string Caminho(string chave)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var nome = new string(chave.Select(c => invalidos.Contains(c) || c == ':' ? '_' : c).ToArray());
        return Path.Combine(_pasta, nome + ".json");
    }
}
