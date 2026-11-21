using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using Transcritor.Dicionario;

namespace Transcritor.Tests;

public sealed class DicionarioTests : IDisposable
{
    // Linhas no formato do wiktextract (kaikki.org), reduzidas ao que o importador usa.
    private const string Ingles = """
        {"word": "peach", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["A fruit."]}], "translations": [{"lang": "Portuguese", "code": "pt", "word": "pêssego", "sense": "fruit"}, {"lang": "Portuguese", "code": "pt", "word": "pessegueiro", "sense": "tree"}, {"lang": "French", "code": "fr", "word": "pêche", "sense": "fruit"}]}
        {"word": "give up", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["To stop."], "translations": [{"lang": "Portuguese", "code": "pt", "word": "desistir", "sense": "stop, quit, desist"}]}]}
        {"word": "give", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["To transfer."]}], "translations": [{"lang": "Portuguese", "code": "pt", "word": "dar", "sense": "transfer"}]}
        {"word": "gave", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["simple past of give"], "form_of": [{"word": "give"}]}]}
        {"word": "went", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["simple past of go"], "form_of": [{"word": "go"}]}]}
        {"word": "talent", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["A skill."]}]}
        """;

    private const string Portugues = """
        {"word": "pêssego", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["peach (fruit)"]}]}
        {"word": "desistir", "pos": "verb", "lang_code": "pt", "senses": [{"glosses": ["to give up"]}]}
        {"word": "desisti", "pos": "verb", "lang_code": "pt", "senses": [{"glosses": ["inflection of desistir:", "first-person singular preterite indicative"], "form_of": [{"word": "desistir"}]}]}
        """;

    private readonly PastaTemporaria _pasta = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _pasta.Dispose();
    }

    private async Task<(DicionarioSqlite Dicionario, ResumoImportacao Resumo)> ImportarAsync()
    {
        var ingles = Path.Combine(_pasta.Caminho, "en.jsonl.gz");
        await using (var gz = new GZipStream(File.Create(ingles), CompressionLevel.Fastest))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(Ingles));
        }

        var portugues = _pasta.Arquivo("pt.jsonl", Portugues);
        var banco = Path.Combine(_pasta.Caminho, "sub", "dic.db");
        var resumo = await ImportadorWiktionary.ImportarAsync(ingles, portugues, banco);
        return (DicionarioSqlite.Abrir(banco), resumo);
    }

    [Fact]
    public async Task Importa_traducoes_definicoes_e_formas()
    {
        var (dicionario, resumo) = await ImportarAsync();
        using var _ = dicionario;

        // peach→pêssego, peach→pessegueiro, give up→desistir, give→dar; e as quatro invertidas.
        Assert.Equal(8, resumo.Traducoes);
        Assert.Equal(2, resumo.Definicoes);
        // gave→give e desisti→desistir; went→go cai porque "go" não tem verbete.
        Assert.Equal(2, resumo.Formas);
        Assert.True(resumo.TamanhoBytes > 0);
        Assert.False(File.Exists(dicionario.Caminho + ".tmp"));
    }

    [Fact]
    public async Task Procura_ingles_para_portugues_com_sentido()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        var verbetes = dicionario.Procurar("Peach,", "en", "pt-BR");

        Assert.Equal(["pêssego", "pessegueiro"], verbetes.Select(v => v.Traducao));
        Assert.Equal("fruit", verbetes[0].Sentido);
        Assert.Equal("substantivo", verbetes[0].Classe);
        Assert.All(verbetes, v => Assert.Equal(TipoVerbete.Traducao, v.Tipo));
        Assert.All(verbetes, v => Assert.Null(v.Lema));
    }

    [Fact]
    public async Task Flexao_procura_a_forma_base()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        var gave = Assert.Single(dicionario.Procurar(" gave", "en", "pt"));
        Assert.Equal(("give", "give", "dar"), (gave.Termo, gave.Lema, gave.Traducao));

        var desisti = dicionario.Procurar("desisti", "pt", "en");
        Assert.All(desisti, v => Assert.Equal("desistir", v.Lema));
        Assert.Contains(desisti, v => v is { Tipo: TipoVerbete.Definicao, Traducao: "to give up" });
        Assert.Contains(desisti, v => v is { Tipo: TipoVerbete.Traducao, Traducao: "give up", Sentido: "stop, quit, desist" });
    }

    [Fact]
    public async Task Portugues_tem_traducao_invertida_antes_da_definicao()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        var verbetes = dicionario.Procurar("PÊSSEGO", "pt", "en");

        Assert.Equal(2, verbetes.Count);
        Assert.Equal((TipoVerbete.Traducao, "peach", "fruit"), (verbetes[0].Tipo, verbetes[0].Traducao, verbetes[0].Sentido));
        Assert.Equal((TipoVerbete.Definicao, "peach (fruit)"), (verbetes[1].Tipo, verbetes[1].Traducao));
    }

    [Fact]
    public async Task Expressao_e_termo_ausente()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        Assert.Equal("desistir", Assert.Single(dicionario.Procurar("give  up", "en", "pt")).Traducao);
        Assert.Empty(dicionario.Procurar("talent", "en", "pt"));
        Assert.Empty(dicionario.Procurar("...", "en", "pt"));
        Assert.Single(dicionario.Procurar("peach", "en", "pt", maximo: 1));
    }

    [Fact]
    public async Task Possessivo_procura_a_palavra_sem_s()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        var verbetes = dicionario.Procurar("peach’s", "en", "pt");

        Assert.Equal(["pêssego", "pessegueiro"], verbetes.Select(v => v.Traducao));
        Assert.All(verbetes, v => Assert.Equal("peach", v.Lema));
    }

    [Fact]
    public async Task Recusa_par_de_idiomas_sem_dados()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        Assert.Throws<ArgumentException>(() => dicionario.Procurar("peach", "en", "en"));
        Assert.Throws<ArgumentException>(() => dicionario.Procurar("peach", "en", "fr"));
    }

    [Fact]
    public void Abrir_sem_arquivo_ou_com_outro_arquivo_explica_como_gerar()
    {
        var ausente = Assert.Throws<FileNotFoundException>(() => DicionarioSqlite.Abrir(Path.Combine(_pasta.Caminho, "nao.db")));
        Assert.Contains("transcritor dicionario importar", ausente.Message, StringComparison.Ordinal);

        var outro = _pasta.Arquivo("outro.db", "não é sqlite");
        var ex = Assert.Throws<FileNotFoundException>(() => DicionarioSqlite.Abrir(outro));
        Assert.Contains("versão", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dump_ausente_gera_erro_com_o_caminho()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => ImportadorWiktionary.ImportarAsync(
            Path.Combine(_pasta.Caminho, "x.jsonl"), Path.Combine(_pasta.Caminho, "y.jsonl"), Path.Combine(_pasta.Caminho, "d.db")));
        Assert.EndsWith("x.jsonl", ex.FileName, StringComparison.Ordinal);
    }
}
