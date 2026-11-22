using System.Text.RegularExpressions;
using Transcritor.Dicionario;

namespace Transcritor.Traducao.Dicionario;

/// <summary>
/// Dica do dicionário para a tradução de frases: os sentidos das palavras raras da frase (fora das
/// <c>limiar</c> mais frequentes da lista de frequência), acrescentados às instruções do prompt. Palavra com
/// maiúscula no meio da frase é tratada como nome e fica de fora, e sentido de nome próprio nunca entra (o dicionário
/// traduzia "James" como "Tiago").
/// Medido em docs/modelos.md: só das raras, acerta vocabulário raro ("porcupine" → "porco-espinho", "gizzard" →
/// "moela"), mas às vezes muda outra parte da frase ("peach tree" → "macieira"); por isso fica desligada por padrão.
/// </summary>
internal static partial class DicaPalavrasRaras
{
    /// <summary>Classe dos nomes no dicionário: nunca vão na dica, nem no começo da frase.</summary>
    private const string ClasseNomeProprio = "nome próprio";

    /// <summary>Teto de traduções por palavra na dica.</summary>
    private const int TraducoesPorPalavra = 6;

    /// <summary>Palavras raras da frase com as traduções do dicionário, na ordem em que aparecem.</summary>
    public static IReadOnlyList<(string Palavra, IReadOnlyList<Verbete> Sentidos)> Achar(
        DicionarioSqlite dicionario, string frase, string origem, string destino, int limiar)
    {
        if (limiar <= 0 || !dicionario.TemFrequencia || string.IsNullOrWhiteSpace(frase))
        {
            return [];
        }

        var achadas = new List<(string, IReadOnlyList<Verbete>)>();
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        var tokens = RegexPalavra().Matches(frase);
        for (var n = 0; n < tokens.Count; n++)
        {
            var token = tokens[n].Value.Trim('\'', '-');
            if (token.Length < 3 || (n > 0 && char.IsUpper(token[0])) || token.Any(char.IsDigit))
            {
                continue;
            }

            // Possessivo vale pela palavra ("nation's" → "nation"); outras contrações ("don't", "isn't") não estão na
            // lista de frequência como uma palavra só e pareciam raras.
            var palavra = token.ToLowerInvariant().Replace('’', '\'');
            if (palavra.EndsWith("'s", StringComparison.Ordinal))
            {
                palavra = palavra[..^2];
            }

            if (palavra.Contains('\'', StringComparison.Ordinal) || palavra.Length < 3)
            {
                continue;
            }

            if (!vistas.Add(palavra) || dicionario.PosicaoFrequencia(palavra, origem) is { } posicao && posicao <= limiar)
            {
                continue;
            }

            var sentidos = dicionario.Procurar(palavra, origem, destino, maximo: 40)
                .Where(v => v.Tipo == TipoVerbete.Traducao && v.Classe != ClasseNomeProprio)
                .DistinctBy(v => v.Traducao.ToLowerInvariant())
                .Take(TraducoesPorPalavra)
                .ToList();
            if (sentidos.Count > 0)
            {
                achadas.Add((palavra, sentidos));
            }
        }

        return achadas;
    }

    /// <summary>Texto acrescentado às instruções do prompt; vazio sem palavras raras.</summary>
    public static string Texto(IReadOnlyList<(string Palavra, IReadOnlyList<Verbete> Sentidos)> achadas) =>
        achadas.Count == 0
            ? string.Empty
            : " Dictionary notes (possible translations; choose the sense that fits the sentence): " + string.Join(' ', achadas.Select(a =>
                $"\"{a.Palavra}\": " + string.Join("; ", a.Sentidos.Select(v =>
                    string.IsNullOrWhiteSpace(v.Sentido) ? v.Traducao : $"{v.Traducao} ({v.Sentido})")) + "."));

    [GeneratedRegex(@"[\w'’-]+")]
    private static partial Regex RegexPalavra();
}
