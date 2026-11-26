using System.Net;
using System.Text.Json;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.Traducao;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

public sealed class TradutorOllamaTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static HttpResponseMessage RespostaChat(string conteudo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new { model = "m", message = new { role = "assistant", content = conteudo }, done = true }));

    /// <summary>Lê o pedido enviado ao /api/chat.</summary>
    private static (string Modelo, List<(string Papel, string Conteudo)> Mensagens, bool TemFormato) LerPedido(string corpo)
    {
        using var doc = JsonDocument.Parse(corpo);
        var raiz = doc.RootElement;
        var mensagens = raiz.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!))
            .ToList();
        Assert.False(raiz.GetProperty("stream").GetBoolean());
        Assert.Equal(0, raiz.GetProperty("options").GetProperty("temperature").GetDouble());
        return (raiz.GetProperty("model").GetString()!, mensagens, raiz.TryGetProperty("format", out _));
    }

    private (TradutorOllama Tradutor, FakeHttpHandler Http) Criar(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, Action<OpcoesTraducao>? ajustar = null)
    {
        var o = new OpcoesTraducao
        {
            OllamaUrl = new Uri("http://ollama.test:11434"),
            CaminhoCachePalavras = Path.Combine(_pasta.Caminho, "palavras.json"),
        };
        ajustar?.Invoke(o);
        var opcoes = Microsoft.Extensions.Options.Options.Create(o);
        var handler = new FakeHttpHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return (new TradutorOllama(new ClienteOllama(http, opcoes), new CachePalavrasEmDisco(opcoes), opcoes), handler);
    }

    [Fact]
    public async Task Preparar_o_tempo_real_liga_o_ollama_e_carrega_o_modelo_das_frases_do_tempo_real()
    {
        var (tradutor, http) = Criar((_, _) => FakeHttpHandler.Json("{}"), o => o.ModeloFrasesTempoReal = "translategemma:4b");

        await tradutor.PrepararTempoRealAsync(carregarModelo: false, CancellationToken.None);
        await tradutor.PrepararTempoRealAsync(carregarModelo: true, CancellationToken.None);

        Assert.Equal("/api/version", http.Recebidas[0].Requisicao.RequestUri!.AbsolutePath);
        Assert.Equal("/api/generate", http.Recebidas[1].Requisicao.RequestUri!.AbsolutePath);
        using var corpo = System.Text.Json.JsonDocument.Parse(http.Recebidas[1].Corpo);
        Assert.Equal("translategemma:4b", corpo.RootElement.GetProperty("model").GetString());
        Assert.Equal("10m", corpo.RootElement.GetProperty("keep_alive").GetString());
        Assert.False(corpo.RootElement.TryGetProperty("prompt", out _)); // só carrega, não gera
    }

    [Fact]
    public async Task TranslateGemma_recebe_vizinhas_e_a_frase_entre_tags_e_devolve_so_a_frase()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("We talked about the square. <t>The bench was painted blue.</t> Kids sit on it."));

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("O banco foi pintado de azul.", "pt", "en", ["Falamos da praça."], ["As crianças sentam nele."]),
            CancellationToken.None);

        Assert.Equal("The bench was painted blue.", traducao);
        var (modelo, mensagens, temFormato) = LerPedido(Assert.Single(http.Recebidas).Corpo);
        Assert.Equal("translategemma:4b", modelo);
        Assert.False(temFormato);
        var (papel, conteudo) = Assert.Single(mensagens);
        Assert.Equal("user", papel);
        Assert.Contains("Brazilian Portuguese (pt-BR) to English (en) translator", conteudo, StringComparison.Ordinal);
        Assert.EndsWith("\n\n\nFalamos da praça.\n<t>O banco foi pintado de azul.</t>\nAs crianças sentam nele.", conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorte_que_engoliu_as_vizinhas_e_descartado()
    {
        var respostas = new Queue<string>([
            "<t>Today we talk about the park bench. It was painted blue last week. Kids sit there after school.</t>",
            "Today we talk about the park bench.",
        ]);
        var (tradutor, http) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("Hoje falamos do banco da praça.", "pt", "en", [], ["Ele foi pintado.", "As crianças sentam nele."]),
            CancellationToken.None);

        Assert.Equal("Today we talk about the park bench.", traducao);
        Assert.Equal(2, http.Recebidas.Count);
    }

    [Fact]
    public async Task Envia_teto_de_tokens()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("Hello."));

        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá.", "pt", "en", [], []), CancellationToken.None);

        using var doc = JsonDocument.Parse(http.Recebidas[0].Corpo);
        var teto = doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32();
        Assert.InRange(teto, 64, 2000);
    }

    [Fact]
    public async Task TranslateGemma_sem_tags_na_resposta_refaz_sem_contexto()
    {
        var respostas = new Queue<string>(["Whole passage translated without tags.", "The bench was painted blue."]);
        var (tradutor, http) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("O banco foi pintado de azul.", "pt", "en", ["Antes."], []), CancellationToken.None);

        Assert.Equal("The bench was painted blue.", traducao);
        Assert.Equal(2, http.Recebidas.Count);
        var segundo = LerPedido(http.Recebidas[1].Corpo).Mensagens[0].Conteudo;
        Assert.DoesNotContain("<t>", segundo, StringComparison.Ordinal);
        Assert.EndsWith("\n\n\nO banco foi pintado de azul.", segundo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateGemma_que_inventa_frases_a_mais_refaz_sem_contexto()
    {
        // Visto no audiolivro: "Hurry!" virou "Apresse-se! Rápido! Corra! Não perca tempo!".
        var respostas = new Queue<string>(["<t>Apresse-se! Rápido! Corra! Não perca tempo!</t>", "Depressa!"]);
        var (tradutor, http) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("Hurry!", "en", "pt", ["Don't wait.", "Now's the time."], []), CancellationToken.None);

        Assert.Equal("Depressa!", traducao);
        Assert.Equal(2, http.Recebidas.Count);
    }

    [Fact]
    public async Task Frases_a_mais_tambem_sem_contexto_ficam_so_as_primeiras()
    {
        var respostas = new Queue<string>(["<t>Apresse-se! Rápido! Corra!</t>", "Apresse-se! Rápido! Corra! Não perca tempo!"]);
        var (tradutor, _) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("Hurry!", "en", "pt", ["Don't wait."], []), CancellationToken.None);

        Assert.Equal("Apresse-se!", traducao);
    }

    [Theory]
    [InlineData("Off you go. Hurry up.", "Pode ir. Rápido. Vai! Corre! Já!", "Pode ir. Rápido.")]
    [InlineData("Hurry!", "Depressa! Vai!", "Depressa! Vai!")]
    public void Limpar_corta_frases_inventadas_alem_de_uma_a_mais(string original, string traducao, string esperado) =>
        Assert.Equal(esperado, Prompts.Limpar(traducao, original));

    [Theory]
    [InlineData("Mrs. Smith went home.", "<t>A Sra. Smith foi para casa.</t>")]
    [InlineData("Off you go. Hurry up.", "<t>Pode ir. Rápido. Vai!</t>")]
    public void Recorte_aceita_ate_uma_frase_a_mais_que_o_original(string original, string resposta) =>
        Assert.NotNull(Prompts.RecortarAlvo(resposta, original));

    [Fact]
    public async Task Modelo_de_instrucao_usa_json_schema_e_le_campo_traducao()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("""{"traducao": "\"Vamos encerrar por hoje.\""}"""), o => o.ModeloFrasesTempoReal = "gemma3:4b");

        var traducao = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("Let's call it a day.", "en", "pt", ["We are tired."], []), CancellationToken.None);

        Assert.Equal("Vamos encerrar por hoje.", traducao);
        var (modelo, mensagens, temFormato) = LerPedido(http.Recebidas[0].Corpo);
        Assert.Equal("gemma3:4b", modelo);
        Assert.True(temFormato);
        Assert.Equal("system", mensagens[0].Papel);
        Assert.Contains("\"antes\":[\"We are tired.\"]", mensagens[1].Conteudo, StringComparison.Ordinal);
        Assert.Contains("\"frase\":\"Let's call it a day.\"", mensagens[1].Conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lote_usa_janela_de_vizinhas_e_reporta_progresso()
    {
        var (tradutor, http) = Criar((_, corpo) =>
        {
            var texto = LerPedido(corpo).Mensagens[0].Conteudo;
            var alvo = System.Text.RegularExpressions.Regex.Match(texto, "<t>(.*?)</t>").Groups[1].Value;
            return RespostaChat($"<t>T{alvo}</t>");
        }, o => { o.FrasesAntes = 1; o.FrasesDepois = 1; });
        var progresso = new List<double>();

        var r = await tradutor.TraduzirFrasesAsync(["a", "b", "c"], "pt", "en", new ProgressoLista(progresso), CancellationToken.None);

        Assert.Equal(["Ta", "Tb", "Tc"], r);
        var textos = http.Recebidas.Select(x => LerPedido(x.Corpo).Mensagens[0].Conteudo).ToList();
        Assert.EndsWith("\n\n\n<t>a</t>\nb", textos[0], StringComparison.Ordinal);
        Assert.EndsWith("\n\n\na\n<t>b</t>\nc", textos[1], StringComparison.Ordinal);
        Assert.EndsWith("\n\n\nb\n<t>c</t>", textos[2], StringComparison.Ordinal);
        Assert.Equal(100, progresso[^1]);
        Assert.All(http.Recebidas, x => Assert.Equal("translategemma:12b", LerPedido(x.Corpo).Modelo));
    }

    [Fact]
    public async Task Frase_avulsa_usa_o_modelo_do_tempo_real_e_arquivo_usa_o_maior()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("Hello."));

        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá.", "pt", "en", [], []), CancellationToken.None);
        await tradutor.TraduzirFrasesAsync(["Olá."], "pt", "en", null, CancellationToken.None);

        Assert.Equal(["translategemma:4b", "translategemma:12b"], http.Recebidas.Select(x => LerPedido(x.Corpo).Modelo));
        Assert.Contains("translategemma:12b", tradutor.Identificador, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_modelo_de_tempo_real_usa_o_do_arquivo()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("Hello."), o => o.ModeloFrasesTempoReal = "");

        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá.", "pt", "en", [], []), CancellationToken.None);

        Assert.Equal("translategemma:12b", LerPedido(http.Recebidas[0].Corpo).Modelo);
    }

    [Fact]
    public async Task Mesmo_idioma_nao_chama_o_ollama()
    {
        var (tradutor, http) = Criar((_, _) => throw new InvalidOperationException("não deveria chamar"));

        Assert.Equal("Olá", await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá", "pt", "pt-BR", [], []), CancellationToken.None));
        Assert.Empty(http.Recebidas);
    }

    [Fact]
    public async Task Palavra_usa_a_traducao_da_frase_e_cache()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("""{"traducao":"bench"}"""));
        var pedido = new PedidoTraducaoPalavra("banco", "O banco foi pintado de azul.", "pt", "en", "The bench was painted blue.");

        var primeira = await tradutor.TraduzirPalavraAsync(pedido, CancellationToken.None);
        var segunda = await tradutor.TraduzirPalavraAsync(pedido with { Palavra = "Banco " }, CancellationToken.None);

        Assert.Equal("bench", primeira);
        Assert.Equal("bench", segunda);
        var (modelo, mensagens, temFormato) = LerPedido(Assert.Single(http.Recebidas).Corpo);
        Assert.Equal("gemma3:4b", modelo);
        Assert.True(temFormato);
        Assert.Contains("\"traducao_da_frase\":\"The bench was painted blue.\"", mensagens[1].Conteudo, StringComparison.Ordinal);
        Assert.Contains("\"palavra\":\"banco\"", mensagens[1].Conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Palavra_sem_traducao_da_frase_traduz_a_frase_antes()
    {
        var respostas = new Queue<string>(["The bank closes at four.", """{"traducao":"bank"}"""]);
        var (tradutor, http) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var r = await tradutor.TraduzirPalavraAsync(new PedidoTraducaoPalavra("banco", "O banco fecha às quatro.", "pt", "en"), CancellationToken.None);

        Assert.Equal("bank", r);
        Assert.Equal(2, http.Recebidas.Count);
        Assert.Equal("translategemma:4b", LerPedido(http.Recebidas[0].Corpo).Modelo);
        Assert.Contains("The bank closes at four.", LerPedido(http.Recebidas[1].Corpo).Mensagens[1].Conteudo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cache_de_palavras_sobrevive_a_nova_instancia()
    {
        var pedido = new PedidoTraducaoPalavra("margem", "Sentamos na margem.", "pt", "en", "We sat on the bank.");
        await Criar((_, _) => RespostaChat("""{"traducao":"bank"}""")).Tradutor.TraduzirPalavraAsync(pedido, CancellationToken.None);

        var (outro, http) = Criar((_, _) => throw new InvalidOperationException("não deveria chamar"));
        Assert.Equal("bank", await outro.TraduzirPalavraAsync(pedido, CancellationToken.None));
        Assert.Empty(http.Recebidas);
    }

    [Fact]
    public async Task Ollama_fora_do_ar_gera_erro_dizendo_qual()
    {
        var (tradutor, _) = Criar((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<ServicoIndisponivelException>(
            () => tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá", "pt", "en", [], []), CancellationToken.None));
        Assert.Equal("Ollama", ex.Servico);
        Assert.Contains("ollama.test:11434", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modelo_ausente_gera_erro_com_comando_de_pull()
    {
        var (tradutor, _) = Criar((_, _) => FakeHttpHandler.Json("""{"error":"model 'translategemma:4b' not found"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<ModeloNaoInstaladoException>(
            () => tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Olá", "pt", "en", [], []), CancellationToken.None));
        Assert.Contains("ollama pull translategemma:4b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verificar_acusa_modelo_de_palavras_ausente()
    {
        var (tradutor, _) = Criar((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/version" => FakeHttpHandler.Json("""{"version":"0.13.0"}"""),
            "/api/tags" => FakeHttpHandler.Json("""{"models":[{"name":"translategemma:12b"},{"name":"translategemma:4b"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var ex = await Assert.ThrowsAsync<ModeloNaoInstaladoException>(() => tradutor.VerificarAsync(CancellationToken.None));
        Assert.Equal("gemma3:4b", ex.Modelo);
    }

    [Theory]
    [InlineData("  \"The bench.\"\n", "O banco.", "The bench.")]
    [InlineData("“Olá”", "Hello", "Olá")]
    [InlineData("\"Hi\" she said", "\"Oi\" ela disse", "\"Hi\" she said")]
    [InlineData("linha um\nlinha dois", "x", "linha um linha dois")]
    public void Limpar_tira_aspas_e_quebras(string traducao, string original, string esperado) =>
        Assert.Equal(esperado, Prompts.Limpar(traducao, original));

    private sealed class ProgressoLista(List<double> lista) : IProgress<double>
    {
        public void Report(double value) => lista.Add(value);
    }
}
