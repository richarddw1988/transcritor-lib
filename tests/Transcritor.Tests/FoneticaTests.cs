using Microsoft.Data.Sqlite;
using Transcritor.Dicionario;

namespace Transcritor.Tests;

public sealed class FoneticaTests : IDisposable
{
    private const string Ingles = """
        {"word": "aunt", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "tia", "sense": "sister of a parent"}, {"code": "pt", "word": "titia", "sense": "affectionate"}]}
        {"word": "aunts", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["plural of aunt"], "form_of": [{"word": "aunt"}]}]}
        {"word": "ant", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "formiga", "sense": "insect"}]}
        {"word": "ants", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["plural of ant"], "form_of": [{"word": "ant"}]}]}
        {"word": "art", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "arte", "sense": "creativity"}]}
        {"word": "arts", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["plural of art"], "form_of": [{"word": "art"}]}]}
        {"word": "night", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "noite", "sense": "darkness"}]}
        {"word": "knight", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "cavaleiro", "sense": "warrior"}]}
        {"word": "give up", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"], "translations": [{"code": "pt", "word": "desistir", "sense": "stop"}]}]}
        """;

    private const string Portugues = """
        {"word": "chuva", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["rain"]}]}
        {"word": "luva", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["glove"]}]}
        """;

    private readonly PastaTemporaria _pasta = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _pasta.Dispose();
    }

    private async Task<(DicionarioSqlite Dicionario, ResumoImportacao Resumo)> ImportarAsync()
    {
        var banco = Path.Combine(_pasta.Caminho, "dic.db");
        var resumo = await ImportadorWiktionary.ImportarAsync(_pasta.Arquivo("en.jsonl", Ingles), _pasta.Arquivo("pt.jsonl", Portugues), banco);
        return (DicionarioSqlite.Abrir(banco), resumo);
    }

    [Theory]
    [InlineData("arts", "en", "ARTS")]
    [InlineData("Aunts,", "en", "ANTS")]
    [InlineData("knight", "en", "NT")]
    [InlineData("chuva", "pt", "XV")]
    [InlineData("xuva", "pt", "XV")]
    public void Chave_de_pronuncia(string palavra, string idioma, string chave) =>
        Assert.Contains(chave, ChaveFonetica.Calcular(palavra, idioma));

    [Theory]
    [InlineData("give up")]
    [InlineData("1984")]
    [InlineData("...")]
    public void Expressao_numero_e_pontuacao_ficam_sem_chave(string palavra) =>
        Assert.Empty(ChaveFonetica.Calcular(palavra, "en"));

    [Theory]
    [InlineData("arts", "aunts", 2)]
    [InlineData("night", "knight", 1)]
    [InlineData("", "abc", 3)]
    [InlineData("igual", "igual", 0)]
    public void Distancia_de_levenshtein(string a, string b, int esperada) =>
        Assert.Equal(esperada, ChaveFonetica.Distancia(a, b));

    [Fact]
    public async Task Parecidas_incluem_flexoes_e_ordenam_pela_soma_das_distancias()
    {
        var (dicionario, resumo) = await ImportarAsync();
        using var _ = dicionario;

        // aunt, aunts, ant, ants, art, arts, night, knight (en) + chuva, luva, tia, titia, formiga, arte, noite, cavaleiro,
        // desistir (pt, traduções invertidas e definições). "give up" fica de fora (expressão).
        Assert.True(resumo.ChavesFoneticas >= 10);

        var arts = dicionario.ProcurarParecidas("arts", "en");
        Assert.DoesNotContain(arts, p => p.Termo == "arts");
        var ants = Assert.Single(arts, p => p.Termo == "ants");
        Assert.Equal((1, 1), (ants.DistanciaPronuncia, ants.DistanciaEscrita));
        var aunts = Assert.Single(arts, p => p.Termo == "aunts");
        Assert.Equal((1, 2), (aunts.DistanciaPronuncia, aunts.DistanciaEscrita));
        Assert.True(arts.ToList().IndexOf(ants) < arts.ToList().IndexOf(aunts)); // soma 2 antes de soma 3

        var knight = dicionario.ProcurarParecidas("night", "en")[0]; // depois vêm "aunt"/"ant" (ANT, a 1 letra de NT)
        Assert.Equal(("knight", 0, 1), (knight.Termo, knight.DistanciaPronuncia, knight.DistanciaEscrita));
    }

    [Fact]
    public async Task Parecidas_em_portugues_maximo_e_sem_resultado()
    {
        var (dicionario, _) = await ImportarAsync();
        using var __ = dicionario;

        var xuva = dicionario.ProcurarParecidas("xuva", "pt");
        Assert.Equal("chuva", xuva[0].Termo); // mesma pronúncia vem antes de "luva" (pronúncia a 1 letra)
        Assert.Contains(xuva, p => p.Termo == "luva");
        Assert.Single(dicionario.ProcurarParecidas("xuva", "pt", maximo: 1));
        Assert.Empty(dicionario.ProcurarParecidas("zzzzqqqq", "en"));
        Assert.Empty(dicionario.ProcurarParecidas("give up", "en"));
    }
}
