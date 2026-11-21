using Transcritor.Core.Erros;

namespace Transcritor.Core;

/// <summary>
/// Decide para qual idioma traduzir. O texto fica sempre no idioma falado;
/// a <see cref="Saida"/> só decide se há tradução e para qual idioma.
/// </summary>
public static class RegraSaida
{
    /// <summary>
    /// Idioma da tradução ("pt" ou "en"), ou nulo quando não é preciso traduzir.
    /// </summary>
    /// <param name="saida">Saída pedida.</param>
    /// <param name="idiomaFalado">Idioma detectado na fala.</param>
    /// <exception cref="TranscritorException">Fala em outro idioma com <see cref="Saida.Ambos"/>.</exception>
    public static string? IdiomaDaTraducao(Saida saida, string idiomaFalado)
    {
        var falado = Idiomas.Normalizar(idiomaFalado);
        return (saida, falado) switch
        {
            (Saida.Ambos, Idiomas.Portugues) => Idiomas.Ingles,
            (Saida.Ambos, Idiomas.Ingles) => Idiomas.Portugues,
            (Saida.Ambos, _) => throw new TranscritorException(
                $"A fala foi detectada como '{falado}', que não é português nem inglês. Use Saida.Portugues ou Saida.Ingles, ou force o idioma."),
            (Saida.Portugues, Idiomas.Portugues) => null,
            (Saida.Portugues, _) => Idiomas.Portugues,
            (Saida.Ingles, Idiomas.Ingles) => null,
            (Saida.Ingles, _) => Idiomas.Ingles,
            _ => throw new ArgumentOutOfRangeException(nameof(saida), saida, null),
        };
    }
}
