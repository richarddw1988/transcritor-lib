using Microsoft.Data.Sqlite;
using Transcritor.Core;

namespace Transcritor.Dicionario;

/// <summary>
/// Consulta o dicionário inglês ↔ português gerado por <see cref="ImportadorWiktionary"/>. Só leitura; pode ser
/// usado por várias threads (cada consulta pega uma conexão do pool).
/// </summary>
public sealed class DicionarioSqlite : IDisposable
{
    private readonly string _conexao;

    private DicionarioSqlite(string conexao)
    {
        _conexao = conexao;
        _temFrequencia = new Lazy<bool>(LerTemFrequencia);
    }

    /// <summary>Caminho do arquivo aberto.</summary>
    public required string Caminho { get; init; }

    /// <summary>Abre um dicionário já importado.</summary>
    /// <exception cref="FileNotFoundException">O arquivo não existe ou não é um dicionário desta lib.</exception>
    public static DicionarioSqlite Abrir(string caminho)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caminho);
        var completo = Path.GetFullPath(caminho);
        if (!File.Exists(completo))
        {
            throw new FileNotFoundException(
                "Dicionário não encontrado. Gere com: transcritor dicionario importar (ver docs/manual.md).", completo);
        }

        var conexao = new SqliteConnectionStringBuilder { DataSource = completo, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var dicionario = new DicionarioSqlite(conexao) { Caminho = completo };
        var versao = dicionario.LerInfo("versao");
        if (versao != ImportadorWiktionary.VersaoFormato.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            dicionario.Dispose();
            throw new FileNotFoundException(
                $"O arquivo não é um dicionário da versão {ImportadorWiktionary.VersaoFormato} (achado: {versao ?? "nenhuma"}). " +
                "Gere de novo com: transcritor dicionario importar.", completo);
        }

        return dicionario;
    }

    /// <summary>
    /// Sentidos de um termo (palavra ou expressão, ex.: "give up"). Procura o termo como está e, se for uma
    /// flexão, também a forma base. Maiúsculas e pontuação em volta são ignoradas.
    /// </summary>
    /// <param name="termo">Palavra ou expressão no idioma de origem.</param>
    /// <param name="origem">Idioma do termo: pt ou en.</param>
    /// <param name="destino">Idioma da tradução: o outro.</param>
    /// <param name="maximo">Teto de verbetes devolvidos.</param>
    public IReadOnlyList<Verbete> Procurar(string termo, string origem, string destino, int maximo = 30)
    {
        ArgumentNullException.ThrowIfNull(termo);
        var o = Idiomas.Normalizar(origem);
        var d = Idiomas.Normalizar(destino);
        if (!((o == Idiomas.Ingles && d == Idiomas.Portugues) || (o == Idiomas.Portugues && d == Idiomas.Ingles)))
        {
            throw new ArgumentException($"O dicionário só tem inglês ↔ português (pedido: {o} → {d}).", nameof(destino));
        }

        var chave = Chave(termo);
        if (chave.Length == 0 || maximo <= 0)
        {
            return [];
        }

        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        var resultado = new List<Verbete>();
        Ler(conexao, o, chave, null, maximo, resultado);

        // Possessivo inglês ("nation's" → "nation"): o Wiktionary não tem verbete para ele.
        if (resultado.Count == 0 && o == Idiomas.Ingles && chave.Length > 2 && chave.EndsWith("'s", StringComparison.Ordinal))
        {
            chave = chave[..^2];
            Ler(conexao, o, chave, chave, maximo, resultado);
        }

        using (var cmd = conexao.CreateCommand())
        {
            cmd.CommandText = "SELECT lema FROM forma WHERE idioma = $i AND chave = $c AND lema <> $c";
            cmd.Parameters.AddWithValue("$i", o);
            cmd.Parameters.AddWithValue("$c", chave);
            var lemas = new List<string>();
            using (var leitor = cmd.ExecuteReader())
            {
                while (leitor.Read())
                {
                    lemas.Add(leitor.GetString(0));
                }
            }

            foreach (var lema in lemas)
            {
                Ler(conexao, o, lema, lema, maximo - resultado.Count, resultado);
            }
        }

        return resultado;
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    /// <summary>
    /// Palavras do dicionário que soam parecido com <paramref name="palavra"/> (chave de pronúncia igual ou a uma letra
    /// de distância), para corrigir uma palavra que o reconhecimento de fala ouviu errado ("arts" → "aunts", "ants").
    /// Ordem: menor soma das distâncias de pronúncia e de escrita, depois pronúncia mais próxima, depois a mais comum
    /// (mais verbetes). A própria palavra não entra. Palavras soltas que têm verbete e as flexões delas.
    /// </summary>
    /// <param name="palavra">Palavra como foi reconhecida.</param>
    /// <param name="idioma">Idioma da palavra: pt ou en.</param>
    /// <param name="maximo">Teto de resultados.</param>
    public IReadOnlyList<Parecida> ProcurarParecidas(string palavra, string idioma, int maximo = 10)
    {
        ArgumentNullException.ThrowIfNull(palavra);
        var i = Idiomas.Normalizar(idioma);
        var termo = Chave(palavra);
        var chaves = ChaveFonetica.Calcular(termo, i);
        if (chaves.Count == 0 || maximo <= 0)
        {
            return [];
        }

        // Chaves próximas: comparação em memória (são poucas dezenas de milhares por idioma, de até 4 letras no inglês).
        var proximas = _chavesPorIdioma.GetOrAdd(i, CarregarChaves)
            .Select(c => (Chave: c, Distancia: chaves.Min(q => ChaveFonetica.Distancia(q, c))))
            .Where(c => c.Distancia <= 1)
            .ToDictionary(c => c.Chave, c => c.Distancia, StringComparer.Ordinal);
        if (proximas.Count == 0)
        {
            return [];
        }

        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        var nomes = proximas.Keys.Select((_, n) => $"$k{n}").ToList();
        cmd.CommandText = $"SELECT chave, termo, sentidos FROM fonetica WHERE idioma = $i AND chave IN ({string.Join(',', nomes)})";
        cmd.Parameters.AddWithValue("$i", i);
        foreach (var (nome, chave) in nomes.Zip(proximas.Keys))
        {
            cmd.Parameters.AddWithValue(nome, chave);
        }

        var achadas = new Dictionary<string, Parecida>(StringComparer.Ordinal);
        using (var leitor = cmd.ExecuteReader())
        {
            while (leitor.Read())
            {
                var t = leitor.GetString(1);
                if (t == termo)
                {
                    continue;
                }

                var nova = new Parecida(t, proximas[leitor.GetString(0)], ChaveFonetica.Distancia(termo, t), leitor.GetInt32(2));
                if (!achadas.TryGetValue(t, out var atual) || nova.DistanciaPronuncia < atual.DistanciaPronuncia)
                {
                    achadas[t] = nova;
                }
            }
        }

        return achadas.Values
            .OrderBy(p => p.DistanciaPronuncia + p.DistanciaEscrita)
            .ThenBy(p => p.DistanciaPronuncia)
            .ThenByDescending(p => p.Sentidos)
            .ThenBy(p => p.Termo, StringComparer.Ordinal)
            .Take(maximo)
            .ToList();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> _chavesPorIdioma =
        new(StringComparer.Ordinal);

    private IReadOnlyList<string> CarregarChaves(string idioma)
    {
        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT chave FROM fonetica WHERE idioma = $i";
        cmd.Parameters.AddWithValue("$i", idioma);
        var chaves = new List<string>();
        using var leitor = cmd.ExecuteReader();
        while (leitor.Read())
        {
            chaves.Add(leitor.GetString(0));
        }

        return chaves;
    }

    /// <summary>O banco foi gerado com listas de frequência (<see cref="ListasFrequencia"/>).</summary>
    public bool TemFrequencia => _temFrequencia.Value;

    private readonly Lazy<bool> _temFrequencia;

    /// <summary>
    /// Posição da palavra na lista de frequência (1 = a mais comum), a melhor entre a palavra e as formas base dela
    /// ("nibbled" vale a posição de "nibble" se for melhor). Nulo se a palavra não está na lista ou o banco não tem
    /// frequência (<see cref="TemFrequencia"/>).
    /// </summary>
    /// <param name="palavra">Palavra solta.</param>
    /// <param name="idioma">Idioma da palavra: pt ou en.</param>
    public int? PosicaoFrequencia(string palavra, string idioma)
    {
        ArgumentNullException.ThrowIfNull(palavra);
        var chave = Chave(palavra);
        if (chave.Length == 0 || !TemFrequencia)
        {
            return null;
        }

        var i = Idiomas.Normalizar(idioma);
        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(posicao) FROM frequencia WHERE idioma = $i AND chave IN
                (SELECT $c UNION SELECT lema FROM forma WHERE idioma = $i AND chave = $c)
            """;
        cmd.Parameters.AddWithValue("$i", i);
        cmd.Parameters.AddWithValue("$c", chave);
        return cmd.ExecuteScalar() is long p ? (int)p : null;
    }

    private bool LerTemFrequencia()
    {
        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM frequencia)";
        return cmd.ExecuteScalar() is long n && n != 0;
    }

    /// <summary>Formas base de uma flexão ("gave" → "give"); vazio se o termo não é flexão conhecida.</summary>
    internal IReadOnlyList<string> Lemas(string termo, string idioma)
    {
        var chave = Chave(termo);
        if (chave.Length == 0)
        {
            return [];
        }

        using var conexao = new SqliteConnection(_conexao);
        conexao.Open();
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = "SELECT lema FROM forma WHERE idioma = $i AND chave = $c";
        cmd.Parameters.AddWithValue("$i", Idiomas.Normalizar(idioma));
        cmd.Parameters.AddWithValue("$c", chave);
        var lemas = new List<string>();
        using var leitor = cmd.ExecuteReader();
        while (leitor.Read())
        {
            lemas.Add(leitor.GetString(0));
        }

        return lemas;
    }

    /// <summary>Chave de busca: minúsculas, sem pontuação nem aspas em volta, apóstrofo tipográfico vira ' .</summary>
    internal static string Chave(string termo)
    {
        var t = termo.Replace('’', '\'').Replace('‘', '\'').Trim()
            .Trim('.', ',', '!', '?', ';', ':', '"', '“', '”', '(', ')', '[', ']', '…', '\'', '-', '—', ' ');
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static void Ler(SqliteConnection conexao, string origem, string chave, string? lema, int maximo, List<Verbete> destino)
    {
        if (maximo <= 0)
        {
            return;
        }

        using var cmd = conexao.CreateCommand();
        cmd.CommandText =
            "SELECT termo, classe, traducao, sentido, tipo FROM verbete WHERE origem = $o AND chave = $c ORDER BY tipo, id LIMIT $m";
        cmd.Parameters.AddWithValue("$o", origem);
        cmd.Parameters.AddWithValue("$c", chave);
        cmd.Parameters.AddWithValue("$m", maximo);
        using var leitor = cmd.ExecuteReader();
        while (leitor.Read())
        {
            destino.Add(new Verbete(
                leitor.GetString(0),
                lema,
                leitor.GetString(1),
                leitor.GetString(2),
                leitor.IsDBNull(3) ? null : leitor.GetString(3),
                (TipoVerbete)leitor.GetInt32(4)));
        }
    }

    private string? LerInfo(string chave)
    {
        try
        {
            using var conexao = new SqliteConnection(_conexao);
            conexao.Open();
            using var cmd = conexao.CreateCommand();
            cmd.CommandText = "SELECT valor FROM info WHERE chave = $c";
            cmd.Parameters.AddWithValue("$c", chave);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
