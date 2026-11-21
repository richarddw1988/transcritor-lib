using Transcritor.Core;

namespace Transcritor.Dicionario;

/// <summary>
/// Chave de pronúncia de uma palavra: palavras que soam parecido têm chaves iguais ou a uma letra de distância.
/// Inglês: Double Metaphone (biblioteca Phonix), chave principal e alternativa ("arts" = ARTS, "aunts" = ANTS).
/// Português: Metaphone pt-BR (biblioteca MetaphonePtBr) ("chuva" e "xuva" = XV).
/// </summary>
internal static class ChaveFonetica
{
    /// <summary>Chaves da palavra (sem repetição); vazio para expressão, número ou idioma sem algoritmo.</summary>
    public static IReadOnlyList<string> Calcular(string palavra, string idioma)
    {
        var p = DicionarioSqlite.Chave(palavra);
        if (p.Length == 0 || p.Contains(' ', StringComparison.Ordinal) || !p.Any(char.IsLetter))
        {
            return [];
        }

        try
        {
            var chaves = Idiomas.Normalizar(idioma) switch
            {
                Idiomas.Ingles => new Phonix.DoubleMetaphone().BuildKeys(p), // instância por chamada: a classe guarda estado
                Idiomas.Portugues => [MetaphonePtBr.Metaphone.GetMetaphoneToken(p)],
                _ => [],
            };
            return chaves.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            // Bibliotecas de terceiros: palavra com caractere inesperado fica sem chave em vez de derrubar a importação.
            return [];
        }
    }

    /// <summary>Distância de Levenshtein (inserção, remoção ou troca de uma letra = 1).</summary>
    public static int Distancia(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var anterior = new int[b.Length + 1];
        var atual = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            anterior[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            atual[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var custo = a[i - 1] == b[j - 1] ? 0 : 1;
                atual[j] = Math.Min(Math.Min(atual[j - 1] + 1, anterior[j] + 1), anterior[j - 1] + custo);
            }

            (anterior, atual) = (atual, anterior);
        }

        return anterior[b.Length];
    }
}
