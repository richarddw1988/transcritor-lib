using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Transcritor.Core;

namespace Transcritor.Dicionario;

/// <summary>Números de uma importação.</summary>
/// <param name="Traducoes">Verbetes de tradução direta (os dois sentidos somados).</param>
/// <param name="Definicoes">Verbetes de definição (palavra em português explicada em inglês).</param>
/// <param name="Formas">Flexões ligadas à forma base ("gave" → "give").</param>
/// <param name="TamanhoBytes">Tamanho do arquivo gerado.</param>
/// <param name="Duracao">Tempo da importação.</param>
/// <param name="ChavesFoneticas">Palavras com chave de pronúncia (para achar palavras de som parecido).</param>
public sealed record ResumoImportacao(long Traducoes, long Definicoes, long Formas, long TamanhoBytes, TimeSpan Duracao, long ChavesFoneticas = 0);

/// <summary>
/// Gera o SQLite do dicionário a partir dos dumps JSONL do Wiktionary em inglês extraídos pelo wiktextract
/// (kaikki.org, licença CC BY-SA 4.0 / GFDL):
/// <list type="bullet">
/// <item>verbetes em inglês (<c>kaikki.org-dictionary-English.jsonl[.gz]</c>): traduções para o português, com o
/// sentido de cada uma; viram en → pt e, invertidas, pt → en;</item>
/// <item>verbetes em português (<c>kaikki.org-dictionary-Portuguese.jsonl[.gz]</c>): definições curtas em inglês;</item>
/// <item>dos dois: flexões que apontam para a forma base (só as de formas base que têm verbete).</item>
/// </list>
/// </summary>
public static class ImportadorWiktionary
{
    /// <summary>Formato do banco; <see cref="DicionarioSqlite.Abrir"/> recusa outro.</summary>
    public const int VersaoFormato = 3;

    private const int TamanhoMaximoDefinicao = 300;

    private static readonly Dictionary<string, string> Classes = new(StringComparer.Ordinal)
    {
        ["noun"] = "substantivo",
        ["verb"] = "verbo",
        ["adj"] = "adjetivo",
        ["adv"] = "advérbio",
        ["name"] = "nome próprio",
        ["phrase"] = "expressão",
        ["prep_phrase"] = "locução",
        ["proverb"] = "provérbio",
        ["intj"] = "interjeição",
        ["prep"] = "preposição",
        ["pron"] = "pronome",
        ["conj"] = "conjunção",
        ["det"] = "determinante",
        ["article"] = "artigo",
        ["num"] = "numeral",
        ["particle"] = "partícula",
        ["contraction"] = "contração",
        ["abbrev"] = "abreviação",
        ["prefix"] = "prefixo",
        ["suffix"] = "sufixo",
    };

    /// <summary>Importa os dois dumps para <paramref name="caminhoBanco"/> (substitui o arquivo se já existir).</summary>
    /// <param name="jsonlIngles">Dump dos verbetes em inglês (.jsonl ou .jsonl.gz).</param>
    /// <param name="jsonlPortugues">Dump dos verbetes em português (.jsonl ou .jsonl.gz).</param>
    /// <param name="caminhoBanco">SQLite a gerar.</param>
    /// <param name="progresso">Recebe mensagens curtas de andamento.</param>
    /// <param name="cancellationToken">Cancela a importação (o arquivo de destino não é tocado).</param>
    public static async Task<ResumoImportacao> ImportarAsync(
        string jsonlIngles,
        string jsonlPortugues,
        string caminhoBanco,
        IProgress<string>? progresso = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var arquivo in new[] { jsonlIngles, jsonlPortugues })
        {
            if (!File.Exists(arquivo))
            {
                throw new FileNotFoundException("Dump do Wiktionary não encontrado (baixe em kaikki.org; ver docs/manual.md).", arquivo);
            }
        }

        var relogio = Stopwatch.StartNew();
        var destino = Path.GetFullPath(caminhoBanco);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        var temporario = destino + ".tmp";
        File.Delete(temporario);

        var conexaoTexto = new SqliteConnectionStringBuilder { DataSource = temporario, Pooling = false }.ToString();
        long traducoes, definicoes, formas, foneticas;
        await using (var conexao = new SqliteConnection(conexaoTexto))
        {
            await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
            Executar(conexao, """
                PRAGMA journal_mode = OFF;
                PRAGMA synchronous = OFF;
                CREATE TABLE info (chave TEXT PRIMARY KEY, valor TEXT NOT NULL);
                CREATE TABLE verbete (
                    id INTEGER PRIMARY KEY,
                    origem TEXT NOT NULL,
                    chave TEXT NOT NULL,
                    termo TEXT NOT NULL,
                    classe TEXT NOT NULL,
                    traducao TEXT NOT NULL,
                    sentido TEXT,
                    tipo INTEGER NOT NULL);
                CREATE TABLE forma (idioma TEXT NOT NULL, chave TEXT NOT NULL, lema TEXT NOT NULL,
                    PRIMARY KEY (idioma, chave, lema)) WITHOUT ROWID;
                """);

            using (var escritor = new Escritor(conexao))
            {
                await LerAsync(jsonlIngles, "inglês", progresso, escritor.ProcessarIngles, cancellationToken).ConfigureAwait(false);
                await LerAsync(jsonlPortugues, "português", progresso, escritor.ProcessarPortugues, cancellationToken).ConfigureAwait(false);
                escritor.Concluir();
                traducoes = escritor.Traducoes;
                definicoes = escritor.Definicoes;
            }

            progresso?.Report("criando índices");
            Executar(conexao, """
                CREATE INDEX ix_verbete ON verbete (origem, chave);
                DELETE FROM forma WHERE NOT EXISTS
                    (SELECT 1 FROM verbete v WHERE v.origem = forma.idioma AND v.chave = forma.lema);
                ANALYZE;
                """);
            formas = Escalar(conexao, "SELECT COUNT(*) FROM forma");

            progresso?.Report("calculando chaves de pronúncia");
            foneticas = GerarFoneticas(conexao);

            using var info = conexao.CreateCommand();
            info.CommandText = "INSERT INTO info (chave, valor) VALUES ($c, $v)";
            var pc = info.Parameters.Add("$c", SqliteType.Text);
            var pv = info.Parameters.Add("$v", SqliteType.Text);
            foreach (var (c, v) in new[]
            {
                ("versao", VersaoFormato.ToString(CultureInfo.InvariantCulture)),
                ("gerado_em", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)),
                ("fonte_ingles", Path.GetFileName(jsonlIngles)),
                ("fonte_portugues", Path.GetFileName(jsonlPortugues)),
                ("licenca", "Wiktionary via wiktextract/kaikki.org — CC BY-SA 4.0 e GFDL"),
            })
            {
                pc.Value = c;
                pv.Value = v;
                info.ExecuteNonQuery();
            }
        }

        File.Move(temporario, destino, overwrite: true);
        return new ResumoImportacao(traducoes, definicoes, formas, new FileInfo(destino).Length, relogio.Elapsed, foneticas);
    }

    private static async Task LerAsync(
        string caminho, string nome, IProgress<string>? progresso, Action<JsonElement> processar, CancellationToken ct)
    {
        await using var arquivo = File.OpenRead(caminho);
        await using Stream entrada = caminho.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(arquivo, CompressionMode.Decompress)
            : arquivo;
        using var leitor = new StreamReader(entrada);
        long linhas = 0;
        while (await leitor.ReadLineAsync(ct).ConfigureAwait(false) is { } linha)
        {
            if (++linhas % 200_000 == 0)
            {
                progresso?.Report($"{nome}: {linhas:N0} verbetes lidos");
            }

            // A maioria das linhas não tem nada útil; evita o custo de interpretar o JSON delas.
            if (!linha.Contains("\"Portuguese\"", StringComparison.Ordinal)
                && !linha.Contains("\"code\": \"pt\"", StringComparison.Ordinal)
                && !linha.Contains("\"form_of\"", StringComparison.Ordinal)
                && !linha.Contains("\"lang_code\": \"pt\"", StringComparison.Ordinal))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(linha);
            processar(doc.RootElement);
        }

        progresso?.Report($"{nome}: {linhas:N0} verbetes lidos");
    }

    /// <summary>
    /// Tabela fonetica: cada palavra (sem espaço) que tem verbete ou é flexão de uma que tem, com suas chaves de
    /// pronúncia e quantos verbetes tem (aproximação de quão comum ela é, usada para ordenar as parecidas).
    /// </summary>
    private static long GerarFoneticas(SqliteConnection conexao)
    {
        Executar(conexao, """
            CREATE TABLE fonetica (idioma TEXT NOT NULL, chave TEXT NOT NULL, termo TEXT NOT NULL, sentidos INTEGER NOT NULL,
                PRIMARY KEY (idioma, chave, termo)) WITHOUT ROWID;
            """);

        var palavras = new List<(string Idioma, string Termo, long Sentidos)>();
        using (var leitura = conexao.CreateCommand())
        {
            // Palavras com verbete e as flexões delas ("aunts" → "aunt"): o reconhecimento devolve a forma flexionada.
            leitura.CommandText = """
                SELECT origem, chave, COUNT(*) FROM verbete WHERE instr(chave, ' ') = 0 GROUP BY origem, chave
                UNION ALL
                SELECT f.idioma, f.chave, (SELECT COUNT(*) FROM verbete v WHERE v.origem = f.idioma AND v.chave = f.lema)
                FROM forma f WHERE instr(f.chave, ' ') = 0
                """;
            using var leitor = leitura.ExecuteReader();
            while (leitor.Read())
            {
                palavras.Add((leitor.GetString(0), leitor.GetString(1), leitor.GetInt64(2)));
            }
        }

        long linhas = 0;
        using var transacao = conexao.BeginTransaction();
        using var cmd = conexao.CreateCommand();
        cmd.Transaction = transacao;
        cmd.CommandText = "INSERT OR IGNORE INTO fonetica (idioma, chave, termo, sentidos) VALUES ($i, $c, $t, $s)";
        var pi = cmd.Parameters.Add("$i", SqliteType.Text);
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
        foreach (var (idioma, termo, sentidos) in palavras)
        {
            var chaves = ChaveFonetica.Calcular(termo, idioma);
            foreach (var chave in chaves)
            {
                pi.Value = idioma;
                pc.Value = chave;
                pt.Value = termo;
                ps.Value = sentidos;
                cmd.ExecuteNonQuery();
            }

            linhas += chaves.Count > 0 ? 1 : 0;
        }

        transacao.Commit();
        return linhas;
    }

    private static void Executar(SqliteConnection conexao, string sql)
    {
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Escalar(SqliteConnection conexao, string sql)
    {
        using var cmd = conexao.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? Texto(JsonElement e, string propriedade) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(propriedade, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static IEnumerable<JsonElement> Lista(JsonElement e, string propriedade) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(propriedade, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string Classe(JsonElement verbete)
    {
        var pos = Texto(verbete, "pos") ?? "?";
        return Classes.TryGetValue(pos, out var classe) ? classe : pos;
    }

    /// <summary>Grava verbetes e formas numa única transação, sem repetir linhas iguais.</summary>
    private sealed class Escritor : IDisposable
    {
        private readonly SqliteTransaction _transacao;
        private readonly SqliteCommand _verbete;
        private readonly SqliteCommand _forma;
        private readonly HashSet<string> _vistos = new(StringComparer.Ordinal);

        public Escritor(SqliteConnection conexao)
        {
            _transacao = conexao.BeginTransaction();
            _verbete = conexao.CreateCommand();
            _verbete.Transaction = _transacao;
            _verbete.CommandText =
                "INSERT INTO verbete (origem, chave, termo, classe, traducao, sentido, tipo) VALUES ($o, $c, $t, $cl, $tr, $s, $ti)";
            foreach (var p in new[] { "$o", "$c", "$t", "$cl", "$tr", "$s" })
            {
                _verbete.Parameters.Add(p, SqliteType.Text);
            }

            _verbete.Parameters.Add("$ti", SqliteType.Integer);

            _forma = conexao.CreateCommand();
            _forma.Transaction = _transacao;
            _forma.CommandText = "INSERT OR IGNORE INTO forma (idioma, chave, lema) VALUES ($i, $c, $l)";
            foreach (var p in new[] { "$i", "$c", "$l" })
            {
                _forma.Parameters.Add(p, SqliteType.Text);
            }
        }

        public long Traducoes { get; private set; }

        public long Definicoes { get; private set; }

        public void ProcessarIngles(JsonElement verbete)
        {
            if (Texto(verbete, "lang_code") is { } codigo && codigo != Idiomas.Ingles)
            {
                return;
            }

            var palavra = Texto(verbete, "word");
            if (string.IsNullOrWhiteSpace(palavra))
            {
                return;
            }

            var classe = Classe(verbete);
            var traducoes = Lista(verbete, "translations")
                .Concat(Lista(verbete, "senses").SelectMany(s => Lista(s, "translations")));
            foreach (var t in traducoes)
            {
                if (Texto(t, "code") != Idiomas.Portugues && Texto(t, "lang") != "Portuguese")
                {
                    continue;
                }

                var traducao = Texto(t, "word");
                if (string.IsNullOrWhiteSpace(traducao))
                {
                    continue;
                }

                var sentido = Texto(t, "sense");
                Inserir(Idiomas.Ingles, palavra, classe, traducao, sentido, TipoVerbete.Traducao);
                Inserir(Idiomas.Portugues, traducao, classe, palavra, sentido, TipoVerbete.Traducao);
            }

            Formas(Idiomas.Ingles, palavra, verbete);
        }

        public void ProcessarPortugues(JsonElement verbete)
        {
            if (Texto(verbete, "lang_code") != Idiomas.Portugues)
            {
                return;
            }

            var palavra = Texto(verbete, "word");
            if (string.IsNullOrWhiteSpace(palavra))
            {
                return;
            }

            var classe = Classe(verbete);
            foreach (var sentido in Lista(verbete, "senses"))
            {
                if (EhFlexao(sentido))
                {
                    continue;
                }

                var definicao = Lista(sentido, "glosses").Select(g => g.GetString()).LastOrDefault(g => !string.IsNullOrWhiteSpace(g));
                if (definicao is null)
                {
                    continue;
                }

                if (definicao.Length > TamanhoMaximoDefinicao)
                {
                    definicao = string.Concat(definicao.AsSpan(0, TamanhoMaximoDefinicao), "…");
                }

                Inserir(Idiomas.Portugues, palavra, classe, definicao, null, TipoVerbete.Definicao);
            }

            Formas(Idiomas.Portugues, palavra, verbete);
        }

        public void Concluir() => _transacao.Commit();

        public void Dispose()
        {
            _verbete.Dispose();
            _forma.Dispose();
            _transacao.Dispose();
        }

        private static bool EhFlexao(JsonElement sentido) =>
            Lista(sentido, "form_of").Any() || Lista(sentido, "alt_of").Any();

        private void Formas(string idioma, string palavra, JsonElement verbete)
        {
            var chave = DicionarioSqlite.Chave(palavra);
            foreach (var sentido in Lista(verbete, "senses"))
            {
                foreach (var alvo in Lista(sentido, "form_of").Concat(Lista(sentido, "alt_of")))
                {
                    var lema = Texto(alvo, "word");
                    if (string.IsNullOrWhiteSpace(lema))
                    {
                        continue;
                    }

                    var chaveLema = DicionarioSqlite.Chave(lema);
                    if (chaveLema.Length == 0 || chaveLema == chave)
                    {
                        continue;
                    }

                    _forma.Parameters["$i"].Value = idioma;
                    _forma.Parameters["$c"].Value = chave;
                    _forma.Parameters["$l"].Value = chaveLema;
                    _forma.ExecuteNonQuery();
                }
            }
        }

        private void Inserir(string origem, string termo, string classe, string traducao, string? sentido, TipoVerbete tipo)
        {
            var chave = DicionarioSqlite.Chave(termo);
            traducao = traducao.Trim();
            if (chave.Length == 0 || traducao.Length == 0
                || !_vistos.Add(string.Join('\u001f', origem, chave, classe, traducao, sentido, tipo)))
            {
                return;
            }

            _verbete.Parameters["$o"].Value = origem;
            _verbete.Parameters["$c"].Value = chave;
            _verbete.Parameters["$t"].Value = termo.Trim();
            _verbete.Parameters["$cl"].Value = classe;
            _verbete.Parameters["$tr"].Value = traducao;
            _verbete.Parameters["$s"].Value = (object?)sentido ?? DBNull.Value;
            _verbete.Parameters["$ti"].Value = (int)tipo;
            _verbete.ExecuteNonQuery();
            if (tipo == TipoVerbete.Traducao)
            {
                Traducoes++;
            }
            else
            {
                Definicoes++;
            }
        }
    }
}
