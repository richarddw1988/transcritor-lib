using System.Globalization;
using System.Text;
using Transcritor.Core.Modelos;

namespace Transcritor.Traducao.Analise;

/// <summary>Bloco como o modelo devolve: texto copiado da frase, sem índices.</summary>
internal sealed record BlocoBruto(string Origem, string Traducao, string Tipo, string Nota);

/// <summary>
/// Liga os blocos do modelo às palavras da frase. O modelo copia o texto do bloco (contar índices ele erra);
/// aqui o texto é procurado em ordem, sem diferenciar maiúsculas e pontuação. Bloco que não for achado é descartado.
/// </summary>
internal static class AlinhadorBlocos
{
    public static IReadOnlyList<Bloco> Alinhar(IReadOnlyList<string> palavras, IEnumerable<BlocoBruto> blocos)
    {
        // Cada palavra vira zero ou mais "tokens" normalizados ("Let's" -> let's; "—" -> nada).
        var tokens = new List<(string Token, int Palavra)>();
        for (var i = 0; i < palavras.Count; i++)
        {
            foreach (var t in Normalizar(palavras[i]))
            {
                tokens.Add((t, i));
            }
        }

        var resultado = new List<Bloco>();
        var cursor = 0;
        foreach (var bloco in blocos)
        {
            var procurados = Normalizar(bloco.Origem);
            if (procurados.Count == 0 || string.IsNullOrWhiteSpace(bloco.Traducao))
            {
                continue;
            }

            var achado = Procurar(tokens, procurados, cursor);
            if (achado < 0)
            {
                continue;
            }

            var inicial = tokens[achado].Palavra;
            var final = tokens[achado + procurados.Count - 1].Palavra;
            cursor = achado + procurados.Count;
            resultado.Add(new Bloco(
                inicial,
                final,
                string.Join(' ', palavras.Skip(inicial).Take(final - inicial + 1)),
                bloco.Traducao.Trim(),
                LerTipo(bloco.Tipo),
                NotaUtil(bloco.Nota)));
        }

        return resultado;
    }

    /// <summary>Frases que o modelo escreve mesmo quando não há nada a explicar (o prompt pede para não escrever).</summary>
    private static readonly string[] NotasVazias =
    [
        "não há armadilha", "nao ha armadilha", "nenhuma armadilha", "sem armadilha", "não há pegadinha",
        "no trap", "there is no trap", "no pitfall",
    ];

    /// <summary>Nula quando vazia ou quando só diz que não há o que explicar.</summary>
    internal static string? NotaUtil(string? nota)
    {
        if (string.IsNullOrWhiteSpace(nota))
        {
            return null;
        }

        var n = nota.Trim();
        return NotasVazias.Any(v => n.Contains(v, StringComparison.OrdinalIgnoreCase)) ? null : n;
    }

    private static int Procurar(List<(string Token, int Palavra)> tokens, List<string> procurados, int desde)
    {
        for (var i = desde; i <= tokens.Count - procurados.Count; i++)
        {
            var igual = true;
            for (var j = 0; j < procurados.Count && igual; j++)
            {
                igual = tokens[i + j].Token == procurados[j];
            }

            if (igual)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Minúsculas, sem acento e sem pontuação; apóstrofos dentro da palavra ficam ("let's").</summary>
    internal static List<string> Normalizar(string texto)
    {
        var limpo = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            limpo.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c is '\'' or '’' ? '\'' : ' ');
        }

        return limpo.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('\''))
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static TipoBloco LerTipo(string tipo) => tipo.Trim().ToLowerInvariant() switch
    {
        "expressao" or "expressão" or "idiom" => TipoBloco.Expressao,
        "phrasal" => TipoBloco.Phrasal,
        "colocacao" or "colocação" or "collocation" => TipoBloco.Colocacao,
        "nome" or "name" => TipoBloco.Nome,
        _ => TipoBloco.Literal,
    };
}
