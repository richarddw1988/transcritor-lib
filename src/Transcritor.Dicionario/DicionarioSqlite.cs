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

    private DicionarioSqlite(string conexao) => _conexao = conexao;

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
