using System.Text.Json;
using Microsoft.Data.Sqlite;
using Transcritor.Core.Contratos;
using Transcritor.Dicionario;
using Transcritor.Traducao;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

/// <summary>Lista de frequência no dicionário e dica das palavras raras na tradução das frases.</summary>
public sealed class DicaPalavrasRarasTests : IDisposable
{
    private const string Ingles = """
        {"word": "porcupine", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "porco-espinho", "sense": "rodent"}, {"code": "pt", "word": "ouriço", "sense": "rodent"}]}
        {"word": "house", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "casa", "sense": "building"}]}
        {"word": "give", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "dar", "sense": "transfer"}]}
        {"word": "gave", "pos": "verb", "lang_code": "en", "senses": [{"glosses": ["simple past of give"], "form_of": [{"word": "give"}]}]}
        {"word": "James", "pos": "name", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "Tiago", "sense": "male given name"}]}
        {"word": "gizzard", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "moela", "sense": "organ"}]}
        """;

    private const string Portugues = """
        {"word": "casa", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["house"]}]}
        """;

    // Formato do FrequencyWords: palavra e contagem, da mais comum para a menos comum.
    private const string FrequenciaIngles = "the 900\nhouse 500\ngive 300\nThe 200\njames 100\ngizzard 2\n";
    private const string FrequenciaPortugues = "casa 10\n";

    private const string Frase = "James gave the porcupine a gizzard near the house.";

    private readonly PastaTemporaria _pasta = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _pasta.Dispose();
    }

    private async Task<string> BancoAsync(bool comFrequencia = true)
    {
        var banco = Path.Combine(_pasta.Caminho, $"dic-{Guid.NewGuid():N}.db");
        var frequencia = comFrequencia
            ? new ListasFrequencia(_pasta.Arquivo("en_50k.txt", FrequenciaIngles), _pasta.Arquivo("pt_br_50k.txt", FrequenciaPortugues))
            : null;
        await ImportadorWiktionary.ImportarAsync(_pasta.Arquivo("en.jsonl", Ingles), _pasta.Arquivo("pt.jsonl", Portugues), banco, frequencia);
        return banco;
    }

    [Fact]
    public async Task Posicao_vem_da_lista_e_a_flexao_usa_a_da_forma_base()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync());

        Assert.True(dicionario.TemFrequencia);
        Assert.Equal(2, dicionario.PosicaoFrequencia("house", "en"));
        Assert.Equal(2, dicionario.PosicaoFrequencia("House.", "en"));
        Assert.Equal(3, dicionario.PosicaoFrequencia("gave", "en")); // "give"
        Assert.Equal(4, dicionario.PosicaoFrequencia("james", "en")); // "The" repetido não conta posição nova
        Assert.Null(dicionario.PosicaoFrequencia("porcupine", "en"));
        Assert.Equal(1, dicionario.PosicaoFrequencia("casa", "pt"));
        Assert.Null(dicionario.PosicaoFrequencia("casa", "en"));
    }

    [Fact]
    public async Task Sem_listas_o_banco_nao_tem_frequencia()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync(comFrequencia: false));

        Assert.False(dicionario.TemFrequencia);
        Assert.Null(dicionario.PosicaoFrequencia("house", "en"));
        Assert.Empty(DicaPalavrasRaras.Achar(dicionario, Frase, "en", "pt", 10000));
    }

    [Fact]
    public async Task Lista_ausente_gera_erro_com_o_caminho()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => ImportadorWiktionary.ImportarAsync(
            _pasta.Arquivo("en.jsonl", Ingles), _pasta.Arquivo("pt.jsonl", Portugues), Path.Combine(_pasta.Caminho, "d.db"),
            new ListasFrequencia(Path.Combine(_pasta.Caminho, "nao.txt"), Path.Combine(_pasta.Caminho, "nao2.txt"))));
        Assert.EndsWith("nao.txt", ex.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task So_palavras_raras_com_verbete_entram_e_nomes_ficam_de_fora()
    {
        using var dicionario = DicionarioSqlite.Abrir(await BancoAsync());

        // Limiar 3: "the", "house" e "gave" (give) são comuns; "gizzard" (posição 5) e "porcupine" (fora da lista) são raras;
        // "James" (posição 4) também, mas só tem sentido de nome próprio; "near" não tem verbete.
        var raras = DicaPalavrasRaras.Achar(dicionario, Frase, "en", "pt", 3);
        Assert.Equal(["porcupine", "gizzard"], raras.Select(r => r.Palavra));
        Assert.Equal(
            " Dictionary notes (possible translations; choose the sense that fits the sentence): " +
            "\"porcupine\": porco-espinho (rodent); ouriço (rodent). \"gizzard\": moela (organ).",
            DicaPalavrasRaras.Texto(raras));

        // Nome no meio da frase não entra, mesmo fora da lista.
        Assert.Empty(DicaPalavrasRaras.Achar(dicionario, "Then James left.", "en", "pt", 1));

        // Contração não entra; possessivo vale pela palavra.
        Assert.Empty(DicaPalavrasRaras.Achar(dicionario, "It isn't the house's door.", "en", "pt", 3));
        Assert.Equal(["porcupine"], DicaPalavrasRaras.Achar(dicionario, "The porcupine's nose.", "en", "pt", 3).Select(r => r.Palavra));
        Assert.Empty(DicaPalavrasRaras.Achar(dicionario, Frase, "en", "pt", 0));
        Assert.Equal(string.Empty, DicaPalavrasRaras.Texto([]));
    }

    [Fact]
    public async Task Frases_do_arquivo_levam_a_dica_e_a_frase_avulsa_nao()
    {
        var banco = await BancoAsync();
        var (tradutor, http) = Criar(banco, limiar: 10000);

        await tradutor.TraduzirFrasesAsync([Frase], "en", "pt", null, CancellationToken.None);
        await tradutor.TraduzirFraseAsync(new PedidoTraducaoFrase(Frase, "en", "pt", [], []), CancellationToken.None);

        Assert.Contains("\"porcupine\": porco-espinho (rodent)", Conteudo(http.Recebidas[0].Corpo), StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary notes", Conteudo(http.Recebidas[1].Corpo), StringComparison.Ordinal);
        Assert.EndsWith(":r10000", tradutor.Identificador, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Limiar_zero_ou_banco_sem_frequencia_desligam_a_dica()
    {
        var (desligado, http) = Criar(await BancoAsync(), limiar: 0);
        var (semLista, http2) = Criar(await BancoAsync(comFrequencia: false), limiar: 10000);

        await desligado.TraduzirFrasesAsync([Frase], "en", "pt", null, CancellationToken.None);
        await semLista.TraduzirFrasesAsync([Frase], "en", "pt", null, CancellationToken.None);

        Assert.DoesNotContain("Dictionary notes", Conteudo(http.Recebidas[0].Corpo), StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary notes", Conteudo(http2.Recebidas[0].Corpo), StringComparison.Ordinal);
        Assert.DoesNotContain(":r", desligado.Identificador, StringComparison.Ordinal);
        Assert.DoesNotContain(":r", semLista.Identificador, StringComparison.Ordinal);
    }

    private (TradutorOllama Tradutor, FakeHttpHandler Http) Criar(string banco, int limiar)
    {
        var opcoes = Microsoft.Extensions.Options.Options.Create(new OpcoesTraducao
        {
            OllamaUrl = new Uri("http://ollama.test:11434"),
            CaminhoCachePalavras = Path.Combine(_pasta.Caminho, $"palavras-{Guid.NewGuid():N}.json"),
            CaminhoDicionario = banco,
            LimiarPalavraRara = limiar,
        });
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(JsonSerializer.Serialize(
            new { model = "m", message = new { role = "assistant", content = "James deu ao porco-espinho uma moela perto da casa." }, done = true })));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return (new TradutorOllama(new ClienteOllama(http, opcoes), new CachePalavrasEmDisco(opcoes), opcoes, new FonteDicionario(opcoes)), handler);
    }

    private static string Conteudo(string corpo)
    {
        using var doc = JsonDocument.Parse(corpo);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    }
}
