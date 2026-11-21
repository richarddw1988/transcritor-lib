using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Monta as frases do JSON a partir dos segmentos e palavras do motor.
/// Cada segmento vira ao menos uma frase; um segmento com mais de uma frase
/// (pontuação final no meio) é dividido.
/// </summary>
internal static class MontadorFrases
{
    private static readonly char[] PontuacaoFinal = ['.', '?', '!', '…'];
    private static readonly char[] FechamentoIgnorado = ['"', '\'', ')', ']', '»', '”', '’'];

    public static IReadOnlyList<Frase> Montar(TranscricaoBruta bruta)
    {
        var palavras = bruta.Palavras
            .Select(p => p with { Texto = p.Texto.Trim() })
            .Where(p => p.Texto.Length > 0)
            .OrderBy(p => p.Inicio)
            .ToList();

        var segmentos = bruta.Segmentos.OrderBy(s => s.Inicio).ToList();
        if (segmentos.Count == 0 && palavras.Count > 0)
        {
            segmentos.Add(new SegmentoBruto(palavras[0].Inicio, palavras[^1].Fim, string.Join(' ', palavras.Select(p => p.Texto))));
        }

        var palavrasPorSegmento = DistribuirPalavras(segmentos, palavras);

        var frases = new List<Frase>();
        for (var i = 0; i < segmentos.Count; i++)
        {
            var segmento = segmentos[i];
            var doSegmento = palavrasPorSegmento[i];

            if (doSegmento.Count == 0)
            {
                var texto = segmento.Texto.Trim();
                if (texto.Length > 0)
                {
                    frases.Add(new Frase(Arredondar(segmento.Inicio), Arredondar(segmento.Fim), texto, null, []));
                }

                continue;
            }

            var grupos = DividirPorPontuacao(doSegmento);
            foreach (var grupo in grupos)
            {
                var texto = grupos.Count == 1 && segmento.Texto.Trim().Length > 0
                    ? segmento.Texto.Trim()
                    : string.Join(' ', grupo.Select(p => p.Texto));
                frases.Add(new Frase(
                    Arredondar(grupo[0].Inicio),
                    Arredondar(grupo[^1].Fim),
                    texto,
                    null,
                    grupo.Select(p => new Palavra(p.Texto, Arredondar(p.Inicio), Arredondar(p.Fim))).ToList()));
            }
        }

        return frases;
    }

    /// <summary>Cada palavra vai para o segmento que contém o meio dela (segmentos e palavras em ordem).</summary>
    private static List<List<Palavra>> DistribuirPalavras(List<SegmentoBruto> segmentos, List<Palavra> palavras)
    {
        var resultado = segmentos.Select(_ => new List<Palavra>()).ToList();
        if (segmentos.Count == 0)
        {
            return resultado;
        }

        var atual = 0;
        foreach (var palavra in palavras)
        {
            var meio = (palavra.Inicio + palavra.Fim) / 2;
            while (atual < segmentos.Count - 1 && meio > segmentos[atual].Fim && meio >= segmentos[atual + 1].Inicio)
            {
                atual++;
            }

            resultado[atual].Add(palavra);
        }

        return resultado;
    }

    private static List<List<Palavra>> DividirPorPontuacao(List<Palavra> palavras)
    {
        var grupos = new List<List<Palavra>>();
        var grupo = new List<Palavra>();
        foreach (var palavra in palavras)
        {
            grupo.Add(palavra);
            if (TerminaFrase(palavra.Texto))
            {
                grupos.Add(grupo);
                grupo = [];
            }
        }

        if (grupo.Count > 0)
        {
            grupos.Add(grupo);
        }

        return grupos;
    }

    private static bool TerminaFrase(string texto)
    {
        var t = texto.TrimEnd(FechamentoIgnorado);
        return t.Length > 0 && Array.IndexOf(PontuacaoFinal, t[^1]) >= 0;
    }

    private static double Arredondar(double segundos) => Math.Round(segundos, 3, MidpointRounding.AwayFromZero);
}
