using System.Text.Json;
using Microsoft.Data.Sqlite;
using Transcritor.Core.Contratos;
using Transcritor.Dicionario;
using Transcritor.Traducao;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

/// <summary>Tradução de palavra com as opções do dicionário (etapa A).</summary>
public sealed class DicionarioNaTraducaoTests : IDisposable
{
    private const string Ingles = """
        {"word": "bank", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "banco", "sense": "institution"}, {"code": "pt", "word": "margem", "sense": "edge of river"}]}
        {"word": "run", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "correr", "sense": "move fast"}]}
        {"word": "running", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["present participle of run"], "form_of": [{"word": "run"}]}]}
        {"word": "run out of", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"], "translations": [{"code": "pt", "word": "ficar sem", "sense": "exhaust supply"}]}]}
        {"word": "give up", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"], "translations": [{"code": "pt", "word": "desistir", "sense": "stop, quit"}]}]}
        {"word": "give", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "dar", "sense": "transfer"}]}
        {"word": "gave", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["simple past of give"], "form_of": [{"word": "give"}]}]}
        """;

    private const string Portugues = """
        {"word": "banco", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["bank (institution)"]}]}
        {"word": "desisti", "pos": "verb", "lang_code": "pt", "senses": [{"glosses": ["inflection of desistir"], "form_of": [{"word": "desistir"}]}]}
        """;

    private readonly PastaTemporaria _pasta = new();
    private string? _banco;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _pasta.Dispose();
    }

    private async Task<string> BancoAsync()
    {
        if (_banco is null)
        {
            _banco = Path.Combine(_pasta.Caminho, "dic.db");
            await ImportadorWiktionary.ImportarAsync(_pasta.Arquivo("en.jsonl", Ingles), _pasta.Arquivo("pt.jsonl", Portugues), _banco);
        }

        return _banco;
    }

    private static HttpResponseMessage RespostaChat(string conteudo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new { model = "m", message = new { role = "assistant", content = conteudo }, done = true }));

    private static string Usuario(string corpo)
    {
        using var doc = JsonDocument.Parse(corpo);
        return doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
    }

    private async Task<(TradutorOllama Tradutor, FakeHttpHandler Http)> CriarAsync(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, bool comDicionario = true)
    {
        var banco = comDicionario ? await BancoAsync() : null;
        var opcoes = Microsoft.Extensions.Options.Options.Create(new OpcoesTraducao
        {
            OllamaUrl = new Uri("http://ollama.test:11434"),
            CaminhoCachePalavras = Path.Combine(_pasta.Caminho, $"palavras-{Guid.NewGuid():N}.json"),
            CaminhoDicionario = banco,
        });
        var handler = new FakeHttpHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        var tradutor = new TradutorOllama(new ClienteOllama(http, opcoes), new CachePalavrasEmDisco(opcoes), opcoes, new FonteDicionario(opcoes));
        return (tradutor, handler);
    }

    [Fact]
    public async Task Candidatos_acham_a_expressao_pela_forma_base_da_palavra()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync());

        var opcoes = CandidatosDicionario.Buscar(dicionario, "running", "We are running out of time.", "en", "pt", "Estamos ficando sem tempo.");

        Assert.Equal(["ficar sem", "correr"], opcoes.Select(o => o.Traducao));
        Assert.Equal("run out of", opcoes[0].Termo);
        Assert.False(opcoes[0].NaTraducao); // "ficando" não tem forma base no dicionário de teste
    }

    [Fact]
    public async Task Candidato_que_aparece_na_traducao_sobe_e_e_marcado_inclusive_conjugado()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync());

        var bank = CandidatosDicionario.Buscar(dicionario, "bank", "We sat on the bank of the river.", "en", "pt", "Sentamos na margem do rio.");
        Assert.Equal(("margem", true), (bank[0].Traducao, bank[0].NaTraducao));
        Assert.False(bank[1].NaTraducao);

        // "gave up" na tradução conta para a opção "give up" (forma base pelo dicionário).
        var desisti = CandidatosDicionario.Buscar(dicionario, "desisti", "Eu desisti.", "pt", "en", "I gave up.");
        Assert.Contains(desisti, o => o is { Traducao: "give up", NaTraducao: true });
    }

    [Fact]
    public async Task So_expressao_devolve_vazio_quando_a_palavra_esta_sozinha()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync());

        Assert.Empty(CandidatosDicionario.Buscar(dicionario, "bank", "The bank is closed.", "en", "pt", "O banco está fechado.", soExpressao: true));
        var gave = CandidatosDicionario.Buscar(dicionario, "gave", "She gave up smoking.", "en", "pt", "Ela parou de fumar.", soExpressao: true);
        Assert.Equal("desistir", Assert.Single(gave).Traducao);
    }

    [Fact]
    public async Task Sem_traducao_da_frase_so_a_expressao_recebe_as_opcoes_numeradas_e_marcadas()
    {
        var respostas = new Queue<string>([
            "Sentamos na margem do rio.", """{"traducao": "margem"}""",
            "Ela resolveu desistir de fumar.", """{"opcao": 1, "traducao": "desistiu"}""",
        ]);
        var (tradutor, http) = await CriarAsync((_, _) => RespostaChat(respostas.Dequeue()));

        var banco = await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("bank", "We sat on the bank of the river.", "en", "pt"), CancellationToken.None);
        var desistiu = await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("gave", "She gave up smoking.", "en", "pt"), CancellationToken.None);

        Assert.Equal("margem", banco);
        Assert.DoesNotContain("opcoes", Usuario(http.Recebidas[1].Corpo), StringComparison.Ordinal);
        Assert.Equal("desistiu", desistiu);
        var usuario = Usuario(http.Recebidas[3].Corpo);
        Assert.Contains("1. give up (verbo): desistir [stop, quit] (in translation)", usuario, StringComparison.Ordinal);
        Assert.Contains("\"opcao\"", http.Recebidas[3].Corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"opcao": 1, "traducao": "gave"}""", "desistir")] // copiou a palavra da frase original
    [InlineData("""{"opcao": 1, "traducao": "Ela desistiu de correr a maratona inteira ontem"}""", "desistir")] // frase inteira
    [InlineData("""{"opcao": 1, "traducao": ""}""", "desistir")]
    [InlineData("""{"opcao": 1, "traducao": "desistiu"}""", "desistiu")] // adaptado à frase: vale
    [InlineData("""{"opcao": 0, "traducao": "largou"}""", "largou")] // nenhuma opção: tradução livre
    public async Task Texto_do_modelo_so_vale_se_parecer_traducao(string resposta, string esperado)
    {
        var respostas = new Queue<string>(["Ela desistiu.", resposta]);
        var (tradutor, _) = await CriarAsync((_, _) => RespostaChat(respostas.Dequeue()));

        var r = await tradutor.TraduzirPalavraAsync(new PedidoTraducaoPalavra("gave", "She gave up.", "en", "pt"), CancellationToken.None);

        Assert.Equal(esperado, r);
    }

    [Fact]
    public async Task Opcao_invalida_sem_texto_aproveitavel_traduz_so_com_o_modelo()
    {
        var respostas = new Queue<string>(["Ela desistiu.", """{"opcao": 9, "traducao": "gave"}""", """{"traducao": "desistiu"}"""]);
        var (tradutor, http) = await CriarAsync((_, _) => RespostaChat(respostas.Dequeue()));

        var r = await tradutor.TraduzirPalavraAsync(new PedidoTraducaoPalavra("gave", "She gave up.", "en", "pt"), CancellationToken.None);

        Assert.Equal("desistiu", r);
        Assert.Equal(3, http.Recebidas.Count);
        Assert.DoesNotContain("opcoes", Usuario(http.Recebidas[2].Corpo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Com_traducao_da_frase_palavra_sozinha_vai_sem_opcoes_e_expressao_vai_com()
    {
        var (tradutor, http) = await CriarAsync((_, _) => RespostaChat("""{"opcao": 1, "traducao": "banco"}"""));

        await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("bank", "The bank is closed.", "en", "pt", "O banco está fechado."), CancellationToken.None);
        await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("gave", "She gave up smoking.", "en", "pt", "Ela parou de fumar."), CancellationToken.None);

        Assert.DoesNotContain("opcoes", Usuario(http.Recebidas[0].Corpo), StringComparison.Ordinal);
        Assert.Contains("1. give up (verbo): desistir [stop, quit]", Usuario(http.Recebidas[1].Corpo), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dicionario_configurado_mas_ausente_traduz_so_com_o_modelo()
    {
        var opcoes = Microsoft.Extensions.Options.Options.Create(new OpcoesTraducao { CaminhoDicionario = Path.Combine(_pasta.Caminho, "nao.db") });
        using var fonte = new FonteDicionario(opcoes);

        Assert.Null(fonte.Dicionario);
    }

    [Theory]
    [InlineData("margem", "Sentamos na margem.", "margem", true)] // igual à opção, mesmo aparecendo na frase
    [InlineData("desisti", "Eu desisti.", "give up", false)]
    [InlineData("Que pena que", "Que pena que você não veio.", "what a pity", false)]
    [InlineData("gave up", "Eu desisti.", "give up", true)]
    [InlineData("um dois três quatro cinco", "x", "dar", false)]
    [InlineData("", "x", "dar", false)]
    public void Aproveitavel(string traducao, string frase, string escolhida, bool esperado) =>
        Assert.Equal(esperado, TradutorOllama.Aproveitavel(traducao, frase, escolhida));
}
