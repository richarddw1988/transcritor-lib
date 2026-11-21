using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Transcritor.Traducao.Cache;

/// <summary>Cache de traduções de palavra solta (palavra + frase + idiomas + modelo).</summary>
public interface ICachePalavras
{
    Task<string?> LerAsync(string chave, CancellationToken cancellationToken);

    Task GravarAsync(string chave, string traducao, CancellationToken cancellationToken);
}

/// <summary>Um único JSON (dicionário chave → tradução), carregado na 1ª leitura e regravado a cada inclusão.</summary>
internal sealed class CachePalavrasEmDisco(IOptions<OpcoesTraducao> opcoes) : ICachePalavras, IDisposable
{
    private readonly string _caminho = opcoes.Value.CaminhoCachePalavras;
    private readonly SemaphoreSlim _trava = new(1, 1);
    private Dictionary<string, string>? _itens;

    public async Task<string?> LerAsync(string chave, CancellationToken cancellationToken)
    {
        await _trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var itens = await CarregarAsync(cancellationToken).ConfigureAwait(false);
            return itens.TryGetValue(chave, out var traducao) ? traducao : null;
        }
        finally
        {
            _trava.Release();
        }
    }

    public async Task GravarAsync(string chave, string traducao, CancellationToken cancellationToken)
    {
        await _trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var itens = await CarregarAsync(cancellationToken).ConfigureAwait(false);
            itens[chave] = traducao;

            var pasta = Path.GetDirectoryName(Path.GetFullPath(_caminho));
            if (!string.IsNullOrEmpty(pasta))
            {
                Directory.CreateDirectory(pasta);
            }

            var temporario = _caminho + ".tmp";
            await File.WriteAllTextAsync(temporario, JsonSerializer.Serialize(itens), cancellationToken).ConfigureAwait(false);
            File.Move(temporario, _caminho, overwrite: true);
        }
        finally
        {
            _trava.Release();
        }
    }

    public void Dispose() => _trava.Dispose();

    private async Task<Dictionary<string, string>> CarregarAsync(CancellationToken cancellationToken)
    {
        if (_itens is not null)
        {
            return _itens;
        }

        _itens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(_caminho))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_caminho, cancellationToken).ConfigureAwait(false);
                var lidos = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (lidos is not null)
                {
                    _itens = new Dictionary<string, string>(lidos, StringComparer.Ordinal);
                }
            }
            catch (JsonException)
            {
                // Cache corrompido: começa vazio.
            }
        }

        return _itens;
    }
}
