using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Transcritor.Core;

namespace Transcritor.Traducao;

/// <summary>Termo com tradução fixa (nome, título, termo técnico): "Giant Peach" → "Pêssego Gigante".</summary>
public sealed class EntradaGlossario
{
    /// <summary>Idioma do termo: pt ou en. A tradução é para o outro.</summary>
    public string Origem { get; set; } = string.Empty;

    /// <summary>Termo como aparece no texto falado (maiúsculas não importam).</summary>
    public string Termo { get; set; } = string.Empty;

    /// <summary>Tradução a usar sempre.</summary>
    public string Traducao { get; set; } = string.Empty;
}

/// <summary>
/// Aplica o glossário do usuário na tradução. Medido no audiolivro (docs/modelos.md): a dica no prompt fez o
/// translategemma usar o termo em 23 de 23 frases; trocar o termo no texto antes de traduzir acertou 21 e fica
/// só como 2ª tentativa, quando a tradução não trouxe o termo.
/// </summary>
internal static class Glossario
{
    /// <summary>Entradas cujo termo aparece no texto (palavra inteira), as mais longas primeiro e sem sobreposição.</summary>
    public static IReadOnlyList<EntradaGlossario> Achar(IEnumerable<EntradaGlossario> glossario, string texto, string origem)
    {
        var idioma = Idiomas.Normalizar(origem);
        var ocupado = new bool[texto.Length];
        var achados = new List<EntradaGlossario>();
        foreach (var entrada in Validas(glossario).Where(e => Idiomas.Normalizar(e.Origem) == idioma).OrderByDescending(e => e.Termo.Length))
        {
            foreach (Match m in Padrao(entrada.Termo).Matches(texto))
            {
                if (Enumerable.Range(m.Index, m.Length).Any(i => ocupado[i]))
                {
                    continue;
                }

                for (var i = m.Index; i < m.Index + m.Length; i++)
                {
                    ocupado[i] = true;
                }

                if (!achados.Contains(entrada))
                {
                    achados.Add(entrada);
                }
            }
        }

        return achados;
    }

    /// <summary>Frase acrescentada às instruções do modelo; vazia sem termos.</summary>
    public static string Dica(IReadOnlyList<EntradaGlossario> achados) =>
        achados.Count == 0
            ? string.Empty
            : " Use exactly these translations for names and terms: " +
              string.Join("; ", achados.Select(e => $"\"{e.Termo.Trim()}\" = \"{e.Traducao.Trim()}\"")) + ".";

    /// <summary>A tradução traz todos os termos do glossário (maiúsculas não importam).</summary>
    public static bool Respeitado(string traducao, IReadOnlyList<EntradaGlossario> achados) =>
        achados.All(e => traducao.Contains(e.Traducao.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Troca cada termo pela tradução no texto original (2ª tentativa).</summary>
    public static string Substituir(string texto, IReadOnlyList<EntradaGlossario> achados) =>
        achados.Aggregate(texto, (t, e) => Padrao(e.Termo).Replace(t, e.Traducao.Trim()));

    /// <summary>Tradução fixa quando a palavra pedida é exatamente um termo do glossário.</summary>
    public static string? TraducaoExata(IEnumerable<EntradaGlossario> glossario, string palavra, string origem)
    {
        var idioma = Idiomas.Normalizar(origem);
        var chave = Normalizar(palavra);
        return Validas(glossario).FirstOrDefault(e => Idiomas.Normalizar(e.Origem) == idioma && Normalizar(e.Termo) == chave)?.Traducao.Trim();
    }

    /// <summary>Resumo do glossário para a chave do cache (muda quando qualquer entrada muda); vazio sem glossário.</summary>
    public static string Assinatura(IEnumerable<EntradaGlossario> glossario)
    {
        var linhas = Validas(glossario)
            .Select(e => $"{Idiomas.Normalizar(e.Origem)}\u001f{Normalizar(e.Termo)}\u001f{e.Traducao.Trim()}")
            .Order(StringComparer.Ordinal)
            .ToList();
        return linhas.Count == 0
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', linhas))))[..12];
    }

    private static IEnumerable<EntradaGlossario> Validas(IEnumerable<EntradaGlossario> glossario) =>
        glossario.Where(e => !string.IsNullOrWhiteSpace(e.Origem) && !string.IsNullOrWhiteSpace(e.Termo) && !string.IsNullOrWhiteSpace(e.Traducao));

    private static Regex Padrao(string termo)
    {
        var partes = termo.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Regex.Escape);
        return new Regex($@"(?<![\p{{L}}\p{{N}}]){string.Join(@"\s+", partes)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalizar(string texto) =>
        string.Join(' ', texto.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '“', '”', '\'').ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
