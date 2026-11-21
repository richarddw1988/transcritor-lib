using Transcritor.Dicionario;

namespace Transcritor.Traducao.Dicionario;

/// <summary>Um sentido oferecido ao modelo como opção numerada.</summary>
/// <param name="Termo">Termo do dicionário (a palavra, a forma base ou a expressão que a contém).</param>
/// <param name="Traducao">Tradução (ou definição) no idioma de destino.</param>
/// <param name="Sentido">Sentido a que a tradução se refere, em inglês; nulo se o dicionário não diz.</param>
/// <param name="Classe">Classe gramatical.</param>
/// <param name="NaTraducao">A tradução aparece na tradução da frase (também conjugada: "gave up" conta para "give up").</param>
internal sealed record Candidato(string Termo, string Traducao, string? Sentido, string Classe, bool NaTraducao = false);

/// <summary>
/// Monta as opções do dicionário para traduzir uma palavra numa frase (sem IA, só regras baratas):
/// <list type="number">
/// <item>procura primeiro a maior expressão da frase que contém a palavra ("call it a day", "run out of"), também com
/// a forma base da palavra ("running" → "run");</item>
/// <item>junta os sentidos da palavra sozinha, sem repetir traduções;</item>
/// <item>marca e sobe para o começo as traduções que aparecem na tradução da frase (a melhor pista do sentido),
/// comparando também com as formas base das palavras da tradução;</item>
/// <item>corta em <c>maximo</c>.</item>
/// </list>
/// Com <c>soExpressao</c>, devolve só os sentidos da expressão (vazio se a palavra não faz parte de uma).
/// </summary>
internal static class CandidatosDicionario
{
    /// <summary>Maior expressão procurada, em palavras.</summary>
    private const int MaiorExpressao = 4;

    public static IReadOnlyList<Candidato> Buscar(
        DicionarioSqlite dicionario,
        string palavra,
        string frase,
        string origem,
        string destino,
        string? traducaoDaFrase,
        bool soExpressao = false,
        int maximo = 10)
    {
        var daPalavra = dicionario.Procurar(palavra, origem, destino, maximo: 40);
        var daExpressao = Expressao(dicionario, palavra, frase, origem, destino, daPalavra);
        if (soExpressao && daExpressao.Count == 0)
        {
            return [];
        }

        // Definições só entram quando a palavra não tem tradução direta.
        var verbetes = soExpressao ? daExpressao : daExpressao.Concat(daPalavra).ToList();
        if (verbetes.Any(v => v.Tipo == TipoVerbete.Traducao))
        {
            verbetes.RemoveAll(v => v.Tipo == TipoVerbete.Definicao);
        }

        var palavrasDaTraducao = PalavrasComFormasBase(dicionario, traducaoDaFrase, destino);
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        return verbetes
            .Where(v => vistos.Add(Normalizar(v.Traducao)))
            .Select((v, ordem) => (Candidato: new Candidato(
                v.Termo, v.Traducao, v.Sentido, v.Classe, ApareceNaTraducao(v.Traducao, palavrasDaTraducao)), Ordem: ordem))
            .OrderByDescending(c => c.Candidato.NaTraducao)
            .ThenBy(c => c.Ordem)
            .Select(c => c.Candidato)
            .Take(maximo)
            .ToList();
    }

    /// <summary>Sentidos da maior expressão (até 4 palavras) da frase que contém a palavra; vazio se não há.</summary>
    private static List<Verbete> Expressao(
        DicionarioSqlite dicionario, string palavra, string frase, string origem, string destino, IReadOnlyList<Verbete> daPalavra)
    {
        var chaves = frase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DicionarioSqlite.Chave).ToArray();
        var alvo = DicionarioSqlite.Chave(palavra);
        var indice = Array.IndexOf(chaves, alvo);
        if (indice < 0 || alvo.Contains(' ', StringComparison.Ordinal))
        {
            return [];
        }

        // "running out of" não existe; "run out of" sim.
        var variantes = new List<string> { alvo }
            .Concat(daPalavra.Where(v => v.Lema is not null).Select(v => v.Lema!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        for (var tamanho = Math.Min(MaiorExpressao, chaves.Length); tamanho >= 2; tamanho--)
        {
            for (var inicio = Math.Max(0, indice - tamanho + 1); inicio <= indice && inicio + tamanho <= chaves.Length; inicio++)
            {
                foreach (var variante in variantes)
                {
                    var partes = chaves.Skip(inicio).Take(tamanho).Select((c, i) => inicio + i == indice ? variante : c).ToList();
                    if (partes.Any(p => p.Length == 0))
                    {
                        continue;
                    }

                    var achados = dicionario.Procurar(string.Join(' ', partes), origem, destino, maximo: 10)
                        .Where(v => v.Tipo == TipoVerbete.Traducao)
                        .ToList();
                    if (achados.Count > 0)
                    {
                        return achados;
                    }
                }
            }
        }

        return [];
    }

    /// <summary>Palavras da tradução da frase e as formas base delas ("gave" → também "give").</summary>
    private static HashSet<string> PalavrasComFormasBase(DicionarioSqlite dicionario, string? traducaoDaFrase, string idioma)
    {
        var palavras = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(traducaoDaFrase))
        {
            return palavras;
        }

        foreach (var p in Normalizar(traducaoDaFrase).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            palavras.Add(p);
            palavras.UnionWith(dicionario.Lemas(p, idioma));
        }

        return palavras;
    }

    /// <summary>Todas as palavras da tradução (até 4, ao menos uma com 3+ letras) estão na tradução da frase.</summary>
    private static bool ApareceNaTraducao(string traducao, HashSet<string> palavrasDaTraducao)
    {
        var partes = Normalizar(traducao).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length is > 0 and <= 4
            && partes.Any(p => p.Length >= 3)
            && partes.All(palavrasDaTraducao.Contains);
    }

    /// <summary>Minúsculas, só letras/dígitos separados por um espaço.</summary>
    private static string Normalizar(string texto)
    {
        var limpo = new string(texto.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(' ', limpo.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
