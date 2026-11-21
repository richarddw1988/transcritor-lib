using Microsoft.Extensions.Options;
using Transcritor.Dicionario;

namespace Transcritor.Traducao.Dicionario;

/// <summary>Abre o dicionário de <see cref="OpcoesTraducao.CaminhoDicionario"/> na 1ª vez; nulo se não configurado ou ausente.</summary>
internal sealed class FonteDicionario(IOptions<OpcoesTraducao> opcoes) : IDisposable
{
    private readonly Lazy<DicionarioSqlite?> _dicionario = new(() => Abrir(opcoes.Value.CaminhoDicionario));

    public DicionarioSqlite? Dicionario => _dicionario.Value;

    public void Dispose()
    {
        if (_dicionario.IsValueCreated)
        {
            _dicionario.Value?.Dispose();
        }
    }

    private static DicionarioSqlite? Abrir(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho))
        {
            return null;
        }

        try
        {
            return DicionarioSqlite.Abrir(caminho);
        }
        catch (FileNotFoundException)
        {
            // Dicionário é opcional: sem ele, a palavra é traduzida só pelo modelo.
            return null;
        }
    }
}
