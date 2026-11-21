using System.Text.Json;
using Transcritor.Core.Contratos;
using Transcritor.Traducao;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

public sealed class GlossarioTests : IDisposable
{
    private static readonly EntradaGlossario[] Termos =
    [
        new() { Origem = "en", Termo = "Giant Peach", Traducao = "Pêssego Gigante" },
        new() { Origem = "en", Termo = "Aunt Spiker", Traducao = "Tia Espeta" },
        new() { Origem = "en", Termo = "Spiker", Traducao = "Espeta" },
        new() { Origem = "pt", Termo = "Pêssego Gigante", Traducao = "Giant Peach" },
    ];

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    [Fact]
    public void Acha_termos_inteiros_sem_sobreposicao_e_so_do_idioma_de_origem()
    {
        var achados = Glossario.Achar(Termos, "AUNT  SPIKER ate the giant peach; Spiker laughed. Peaches!", "en-US");

        // "Aunt Spiker" (mais longo) cobre o "Spiker" de dentro; o "Spiker" solto conta separado.
        Assert.Equal(["Aunt Spiker", "Giant Peach", "Spiker"], achados.Select(e => e.Termo).Order(StringComparer.Ordinal));
        Assert.Empty(Glossario.Achar(Termos, "The peaches were giant.", "en"));
        Assert.Empty(Glossario.Achar(Termos, "Giant Peach", "pt"));
    }

    [Fact]
    public void Dica_substituicao_conferencia_e_traducao_exata()
    {
        var achados = Glossario.Achar(Termos, "James and the Giant Peach", "en");

        Assert.Equal(" Use exactly these translations for names and terms: \"Giant Peach\" = \"Pêssego Gigante\".", Glossario.Dica(achados));
        Assert.Equal(string.Empty, Glossario.Dica([]));
        Assert.Equal("James and the Pêssego Gigante", Glossario.Substituir("James and the giant peach", achados));
        Assert.True(Glossario.Respeitado("James e o pêssego gigante", achados));
        Assert.False(Glossario.Respeitado("James e a Gigante Pera", achados));
        Assert.Equal("Pêssego Gigante", Glossario.TraducaoExata(Termos, " giant peach,", "en"));
        Assert.Null(Glossario.TraducaoExata(Termos, "peach", "en"));
    }

    [Fact]
    public void Assinatura_muda_com_o_glossario_e_e_vazia_sem_ele()
    {
        var a = Glossario.Assinatura(Termos);

        Assert.Equal(12, a.Length);
        Assert.Equal(a, Glossario.Assinatura(Termos.Reverse()));
        Assert.NotEqual(a, Glossario.Assinatura(Termos.Take(2)));
        Assert.Equal(string.Empty, Glossario.Assinatura([]));
        Assert.Equal(string.Empty, Glossario.Assinatura([new EntradaGlossario { Origem = "en", Termo = "x" }]));
    }

    private static HttpResponseMessage RespostaChat(string conteudo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new { model = "m", message = new { role = "assistant", content = conteudo }, done = true }));

    private static string Conteudo(string corpo, int mensagem)
    {
        using var doc = JsonDocument.Parse(corpo);
        return doc.RootElement.GetProperty("messages")[mensagem].GetProperty("content").GetString()!;
    }

    private (TradutorOllama Tradutor, FakeHttpHandler Http) Criar(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, Action<OpcoesTraducao>? ajustar = null)
    {
        var o = new OpcoesTraducao
        {
            OllamaUrl = new Uri("http://ollama.test:11434"),
            CaminhoCachePalavras = Path.Combine(_pasta.Caminho, $"p-{Guid.NewGuid():N}.json"),
        };
        foreach (var t in Termos)
        {
            o.Glossario.Add(t);
        }

        ajustar?.Invoke(o);
        var opcoes = Microsoft.Extensions.Options.Options.Create(o);
        var handler = new FakeHttpHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return (new TradutorOllama(new ClienteOllama(http, opcoes), new CachePalavrasEmDisco(opcoes), opcoes), handler);
    }

    [Fact]
    public async Task TranslateGemma_recebe_a_dica_e_nao_refaz_quando_o_termo_veio()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("Antes. <t>James e o Pêssego Gigante.</t>"));

        var r = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("James and the Giant Peach.", "en", "pt", ["Before."], []), CancellationToken.None);

        Assert.Equal("James e o Pêssego Gigante.", r);
        var prompt = Conteudo(Assert.Single(http.Recebidas).Corpo, 0);
        Assert.Contains("commentary. Use exactly these translations for names and terms: \"Giant Peach\" = \"Pêssego Gigante\". Please translate", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Termo_ignorado_refaz_com_o_termo_trocado_e_sem_vizinhas()
    {
        var respostas = new Queue<string>(["<t>James e a Gigante Pera.</t>", "James e o Pêssego Gigante."]);
        var (tradutor, http) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var r = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("James and the Giant Peach.", "en", "pt", ["Before."], ["After."]), CancellationToken.None);

        Assert.Equal("James e o Pêssego Gigante.", r);
        var segunda = Conteudo(http.Recebidas[1].Corpo, 0);
        Assert.EndsWith("\n\n\nJames and the Pêssego Gigante.", segunda, StringComparison.Ordinal);
        Assert.DoesNotContain("Before.", segunda, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segunda_tentativa_que_tambem_falha_mantem_a_primeira()
    {
        var respostas = new Queue<string>(["James e a Gigante Pera.", "James e o pêssego enorme."]);
        var (tradutor, _) = Criar((_, _) => RespostaChat(respostas.Dequeue()));

        var r = await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("James and the Giant Peach.", "en", "pt", [], []), CancellationToken.None);

        Assert.Equal("James e a Gigante Pera.", r);
    }

    [Fact]
    public async Task Modelo_de_instrucao_recebe_a_dica_no_sistema()
    {
        var (tradutor, http) = Criar(
            (_, _) => RespostaChat("""{"traducao":"Tia Espeta riu."}"""),
            o => o.ModeloFrasesTempoReal = "gemma3:4b");

        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Aunt Spiker laughed.", "en", "pt", [], []), CancellationToken.None);

        Assert.EndsWith("\"Aunt Spiker\" = \"Tia Espeta\".", Conteudo(http.Recebidas[0].Corpo, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frase_sem_termo_vai_sem_dica_e_palavra_igual_ao_termo_nao_chama_o_modelo()
    {
        var (tradutor, http) = Criar((_, _) => RespostaChat("Oi."));

        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase("Hi.", "en", "pt", [], []), CancellationToken.None);
        var palavra = await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("Giant Peach", "James and the Giant Peach.", "en", "pt", "James e o Pêssego Gigante."), CancellationToken.None);

        Assert.DoesNotContain("Use exactly", Conteudo(Assert.Single(http.Recebidas).Corpo, 0), StringComparison.Ordinal);
        Assert.Equal("Pêssego Gigante", palavra);
    }

    [Fact]
    public void Identificador_muda_com_o_glossario()
    {
        var (com, _) = Criar((_, _) => RespostaChat(""));
        var (sem, _) = Criar((_, _) => RespostaChat(""), o => o.Glossario.Clear());

        Assert.Matches(":g[0-9a-f]{12}$", com.Identificador);
        Assert.DoesNotContain(":g", sem.Identificador, StringComparison.Ordinal);
    }
}
