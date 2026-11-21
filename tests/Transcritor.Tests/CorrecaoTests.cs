using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;
using Transcritor.Traducao;
using Transcritor.Traducao.Correcoes;
using Transcritor.Traducao.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

public class TrocasDaCorrecaoTests
{
    private static string[] Palavras(string frase) => frase.Split(' ');

    [Fact]
    public void Aceita_troca_por_palavra_de_som_parecido_mantendo_pontuacao_e_maiuscula()
    {
        var trocas = CorretorOllama.Trocas(Palavras("Night rode through the forest at night."), "Knight rode through the forest at night", "en");

        var troca = Assert.Single(trocas);
        Assert.Equal(new Correcao(0, "Night", "Knight"), troca);
    }

    [Fact]
    public void Mantem_a_pontuacao_colada_na_palavra_trocada()
    {
        var trocas = CorretorOllama.Trocas(Palavras("O conserto do carro e o conserto de música."), "O conserto do carro e o concerto de música.", "pt");

        Assert.Equal(new Correcao(6, "conserto", "concerto"), Assert.Single(trocas));
        Assert.Equal(new Correcao(1, "sela.", "cela."), Assert.Single(CorretorOllama.Trocas(Palavras("A sela."), "A cela.", "pt")));
    }

    [Fact]
    public void Recusa_troca_que_nao_soa_parecido()
    {
        Assert.Empty(CorretorOllama.Trocas(Palavras("The knight rode at night."), "The cat rode at night.", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("Estava de mau humor."), "Estava de bom humor.", "pt"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("A cela da prisão"), "A praia da prisão", "pt"));
    }

    [Fact]
    public void Recusa_troca_de_palavra_curta_que_vira_correcao_de_gramatica()
    {
        Assert.Empty(CorretorOllama.Trocas(Palavras("because they was running"), "because they were running", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("Ele não é mau"), "Ele não é mal", "pt"));
    }

    [Fact]
    public void Aceita_chave_a_uma_letra_quando_a_chave_e_longa()
    {
        Assert.Equal(new Correcao(4, "pawns.", "paws."), Assert.Single(CorretorOllama.Trocas(Palavras("walked away with bear pawns."), "walked away with bear paws.", "en")));
    }

    [Fact]
    public void Recusa_flexao_ou_parte_da_mesma_palavra()
    {
        Assert.Empty(CorretorOllama.Trocas(Palavras("You take it"), "You taken it", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("Add the fingers of"), "Add the finger of", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("And slightly mildewed"), "And lightly mildewed", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("The old man whispered."), "The old man whisper'd.", "en"));
        Assert.True(CorretorOllama.MesmaPalavra("Giant", "Giants", "en", null));
        Assert.False(CorretorOllama.MesmaPalavra("night", "knight", "en", null));
    }

    [Fact]
    public void So_troca_as_palavras_permitidas()
    {
        var trocas = CorretorOllama.Trocas(Palavras("The night rode at night."), "The knight rode at knight.", "en", podeTrocar: i => i == 1);

        Assert.Equal(new Correcao(1, "night", "knight"), Assert.Single(trocas));
    }

    [Fact]
    public void Aceita_troca_que_so_muda_apostrofo()
    {
        Assert.Equal(new Correcao(1, "aunt's", "aunts"), Assert.Single(CorretorOllama.Trocas(Palavras("My aunt's live near"), "My aunts live near", "en")));
    }

    [Fact]
    public void Ignora_palavras_acrescentadas_ou_removidas_pelo_modelo()
    {
        Assert.Empty(CorretorOllama.Trocas(Palavras("He gave her up smoking"), "He gave up smoking", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("walked away with bear paws"), "walked away with the bear paws", "en"));
    }

    [Fact]
    public void Recusa_numero_e_expressao_no_lugar_de_palavra()
    {
        Assert.False(CorretorOllama.SoaParecido("four", "4", "en"));
        Assert.Empty(CorretorOllama.Trocas(Palavras("I usually walk to work"), "I usually walk-to work", "en"));
    }
}

public class CorretorOllamaTests
{
    private static CorretorOllama Criar(FakeHttpHandler handler, bool ativo = true)
    {
        var opcoes = Options.Create(new OpcoesTraducao { CorrigirTranscricao = ativo, ModeloCorrecao = "gemma3:12b" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return new CorretorOllama(new ClienteOllama(http, opcoes), opcoes, new FonteDicionario(opcoes));
    }

    private static HttpResponseMessage Resposta(string conteudo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new { message = new { role = "assistant", content = conteudo }, done = true }));

    /// <summary>Responde a correção com <paramref name="corrigida"/> e a verificação com <paramref name="escolha"/>.</summary>
    private static FakeHttpHandler Modelo(string corrigida, int escolha) => new((_, corpo) =>
        Resposta(corpo.Contains("escolha", StringComparison.Ordinal) ? $"{{\"escolha\": {escolha}}}" : $"{{\"corrigida\": \"{corrigida}\"}}"));

    private static readonly PedidoCorrecao Pedido = new(
        "The night rode through the forest.".Split(' '), "en", ["Once upon a time."], ["He was brave."]);

    [Fact]
    public async Task Envia_frase_e_contexto_com_esquema_e_devolve_as_trocas_travadas()
    {
        var handler = Modelo("The knight rode through the woods.", escolha: 2);

        var trocas = await Criar(handler).CorrigirAsync(Pedido, CancellationToken.None);

        // "forest." → "woods." não soa parecido: fica de fora; "night" → "knight" vai à verificação e passa.
        Assert.Equal(new Correcao(1, "night", "knight"), Assert.Single(trocas));
        Assert.Equal(2, handler.Recebidas.Count);
        using var verificacao = JsonDocument.Parse(handler.Recebidas[1].Corpo);
        var pergunta = verificacao.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"opcao1\":\"The night rode through the forest.\"", pergunta, StringComparison.Ordinal);
        Assert.Contains("\"opcao2\":\"The knight rode through the forest.\"", pergunta, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(handler.Recebidas[0].Corpo);
        Assert.Equal("gemma3:12b", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.TryGetProperty("format", out _));
        var usuario = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"frase\":\"The night rode through the forest.\"", usuario, StringComparison.Ordinal);
        Assert.Contains("Once upon a time.", usuario, StringComparison.Ordinal);
        Assert.Contains("He was brave.", usuario, StringComparison.Ordinal);
        Assert.Contains("English", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Troca_que_a_verificacao_recusa_fica_de_fora()
    {
        var handler = Modelo("The knight rode through the forest.", escolha: 1);

        Assert.Empty(await Criar(handler).CorrigirAsync(Pedido, CancellationToken.None));
        Assert.Equal(2, handler.Recebidas.Count);
    }

    [Fact]
    public async Task Verifica_uma_troca_por_vez_sobre_a_frase_com_as_ja_aceitas()
    {
        var handler = Modelo("It was quite quite in the library.", escolha: 2);
        var pedido = new PedidoCorrecao("It was quiet quiet in the library.".Split(' '), "en", [], []);

        await Criar(handler).CorrigirAsync(pedido, CancellationToken.None);

        Assert.Equal(3, handler.Recebidas.Count);
        var segunda = JsonDocument.Parse(handler.Recebidas[2].Corpo).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"opcao1\":\"It was quite quiet in the library.\"", segunda, StringComparison.Ordinal);
        Assert.Contains("\"opcao2\":\"It was quite quite in the library.\"", segunda, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frase_sem_palavra_duvidosa_nao_vai_ao_modelo()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("não deveria chamar"));
        var confiantes = Pedido with { Confiancas = [0.99, 0.97, 0.99, 0.99, 0.99, 0.98] };

        Assert.Empty(await Criar(handler).CorrigirAsync(confiantes, CancellationToken.None));
        Assert.Empty(handler.Recebidas);
    }

    [Fact]
    public async Task Palavra_confiante_nao_e_trocada_mesmo_que_o_modelo_troque()
    {
        var handler = Modelo("The knight rode through the forest.", escolha: 2);
        var pedido = new PedidoCorrecao("The night rode through the forest.".Split(' '), "en", [], [], [0.99, 0.99, 0.99, 0.99, 0.99, 0.6]);

        // "forest." (0,6) leva a frase ao modelo, mas "night" (0,99) não pode ser trocada.
        var trocas = await Criar(handler).CorrigirAsync(pedido, CancellationToken.None);

        Assert.Empty(trocas);
        Assert.Single(handler.Recebidas);
    }

    [Fact]
    public async Task Resposta_fora_do_esquema_nao_troca_nada()
    {
        var trocas = await Criar(new FakeHttpHandler((_, _) => Resposta("{\"corrigida\": \"cortad"))).CorrigirAsync(Pedido, CancellationToken.None);

        Assert.Empty(trocas);
    }

    [Fact]
    public async Task Desligado_nao_verifica_o_modelo()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("não deveria chamar"));
        var corretor = Criar(handler, ativo: false);

        Assert.False(corretor.Ativo);
        Assert.Contains(":r", corretor.Identificador, StringComparison.Ordinal);
        await corretor.VerificarAsync(CancellationToken.None);
        Assert.Empty(handler.Recebidas);
    }
}

/// <summary>Corretor falso: troca as palavras do dicionário dado e guarda os pedidos.</summary>
internal sealed class FakeCorretor(Dictionary<string, string> trocas, bool ativo = true) : ICorretorTranscricao
{
    public List<PedidoCorrecao> Pedidos { get; } = [];

    public string Identificador => "fake-correcao";

    public bool Ativo => ativo;

    public Task<IReadOnlyList<Correcao>> CorrigirAsync(PedidoCorrecao pedido, CancellationToken cancellationToken)
    {
        Pedidos.Add(pedido);
        IReadOnlyList<Correcao> resultado = pedido.Palavras
            .Select((p, i) => trocas.TryGetValue(p, out var nova) ? new Correcao(i, p, nova) : null)
            .OfType<Correcao>()
            .ToList();
        return Task.FromResult(resultado);
    }
}

public sealed class TranscritorArquivoCorrecaoTests : IDisposable
{
    private static readonly TranscricaoBruta Bruta = new("en", 4.0, [],
    [
        new Palavra(" The", 0, 0.3, 0.99), new Palavra(" night", 0.3, 0.6, 0.51), new Palavra(" rode.", 0.6, 0.9, 0.98),
        new Palavra(" Good", 1.0, 1.3), new Palavra(" luck.", 1.3, 1.7),
    ]);

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private TranscritorArquivo Criar(FakeCorretor corretor, FakeTradutor tradutor)
    {
        var opcoes = Opcoes.Arquivo(o => o.PastaCache = Path.Combine(_pasta.Caminho, "cache"));
        return new TranscritorArquivo(
            new FakeConversor(new InfoMidia(4, "mp3", 1)), new FakeMotor(Bruta), new CacheEmDisco(opcoes), opcoes, tradutor, corretor: corretor);
    }

    [Fact]
    public async Task Corrige_antes_de_traduzir_guarda_a_palavra_ouvida_e_usa_o_cache()
    {
        var corretor = new FakeCorretor(new() { ["night"] = "knight" });
        var tradutor = new FakeTradutor();
        var progresso = new ProgressoSincrono();
        var midia = _pasta.Arquivo("a.mp3", "x");

        var r = await Criar(corretor, tradutor).ProcessarAsync(midia, Saida.Ambos, progresso);

        Assert.Equal(2, corretor.Pedidos.Count);
        Assert.Equal(["Good luck."], corretor.Pedidos[0].Seguintes);
        Assert.Equal(["The night rode."], corretor.Pedidos[1].Anteriores);
        Assert.Equal([0.99, 0.51, 0.98], corretor.Pedidos[0].Confiancas!);
        Assert.Equal("The knight rode.", r.Frases[0].Texto);
        Assert.Equal("[en>pt] The knight rode.", r.Frases[0].Traducao);
        var palavra = r.Frases[0].Palavras[1];
        Assert.Equal(("knight", "night", 0.51), (palavra.Texto, palavra.Ouvida, palavra.Confianca));
        Assert.Null(r.Frases[0].Palavras[0].Ouvida);
        Assert.Contains(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.CorrigindoTranscricao && p.Percentual == 100);

        var json = await File.ReadAllTextAsync(OpcoesArquivo.CaminhoJsonAoLado(midia));
        Assert.Contains("\"ouvida\": \"night\"", json, StringComparison.Ordinal);

        // 2ª vez: correção e tradução vêm do cache.
        var deNovo = await Criar(corretor, tradutor).ProcessarAsync(midia, Saida.Ambos);
        Assert.Equal(2, corretor.Pedidos.Count);
        Assert.Equal("The knight rode.", deNovo.Frases[0].Texto);
    }

    [Fact]
    public async Task Desligado_nao_corrige()
    {
        var corretor = new FakeCorretor(new() { ["night"] = "knight" }, ativo: false);

        var r = await Criar(corretor, new FakeTradutor()).ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ingles);

        Assert.Empty(corretor.Pedidos);
        Assert.Equal("The night rode.", r.Frases[0].Texto);
    }
}
