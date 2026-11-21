using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;
using Transcritor.Traducao;
using Transcritor.Traducao.Analise;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

public class AlinhadorBlocosTests
{
    private static readonly string[] Palavras = "Let's call it a day, we are running out of time.".Split(' ');

    [Fact]
    public void Liga_blocos_as_palavras_ignorando_caixa_e_pontuacao()
    {
        var blocos = AlinhadorBlocos.Alinhar(Palavras, [
            new BlocoBruto("let's call it a day", "Vamos encerrar por hoje", "expressao", "Encerrar o trabalho."),
            new BlocoBruto("we are", "estamos", "literal", ""),
            new BlocoBruto("running out of time.", "ficando sem tempo", "phrasal", " "),
        ]);

        Assert.Equal(3, blocos.Count);
        Assert.Equal((0, 4), (blocos[0].PalavraInicial, blocos[0].PalavraFinal));
        Assert.Equal("Let's call it a day,", blocos[0].Texto);
        Assert.Equal(TipoBloco.Expressao, blocos[0].Tipo);
        Assert.Equal("Encerrar o trabalho.", blocos[0].Nota);
        Assert.Equal((7, 10), (blocos[2].PalavraInicial, blocos[2].PalavraFinal));
        Assert.Equal(TipoBloco.Phrasal, blocos[2].Tipo);
        Assert.Null(blocos[2].Nota);
    }

    [Fact]
    public void Descarta_bloco_que_nao_existe_ou_esta_fora_de_ordem()
    {
        var blocos = AlinhadorBlocos.Alinhar(Palavras, [
            new BlocoBruto("we are", "estamos", "literal", ""),
            new BlocoBruto("call it a day", "encerrar", "expressao", ""),
            new BlocoBruto("out of money", "sem dinheiro", "phrasal", ""),
            new BlocoBruto("time", "tempo", "literal", ""),
        ]);

        Assert.Equal(["we are", "time."], blocos.Select(b => b.Texto));
    }

    [Fact]
    public void Ignora_acentos_e_apostrofo_tipografico()
    {
        var blocos = AlinhadorBlocos.Alinhar("Você está à vontade, né?".Split(' '), [
            new BlocoBruto("voce esta a vontade", "you feel at home", "expressao", ""),
            new BlocoBruto("ne", "right", "literal", ""),
        ]);

        Assert.Equal((0, 3), (blocos[0].PalavraInicial, blocos[0].PalavraFinal));
        Assert.Equal("né?", blocos[1].Texto);
    }

    [Theory]
    [InlineData("Nome da cidade. Não há armadilha.", null)]
    [InlineData("There is no trap here.", null)]
    [InlineData("  ", null)]
    [InlineData("'seat' aqui é lugar, não assento.", "'seat' aqui é lugar, não assento.")]
    public void Descarta_nota_que_so_diz_que_nao_ha_o_que_explicar(string nota, string? esperado) =>
        Assert.Equal(esperado, AlinhadorBlocos.NotaUtil(nota));

    [Theory]
    [InlineData("colocacao", TipoBloco.Colocacao)]
    [InlineData("nome", TipoBloco.Nome)]
    [InlineData("qualquer", TipoBloco.Literal)]
    public void Le_o_tipo(string tipo, TipoBloco esperado) =>
        Assert.Equal(esperado, AlinhadorBlocos.Alinhar(["x"], [new BlocoBruto("x", "y", tipo, "")])[0].Tipo);
}

public sealed class AnalisadorOllamaTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static AnalisadorOllama Criar(FakeHttpHandler handler, bool ativo = true)
    {
        var opcoes = Options.Create(new OpcoesTraducao { AnalisarSentido = ativo, ModeloAnalise = "gemma3:12b" });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return new AnalisadorOllama(new ClienteOllama(http, opcoes), opcoes);
    }

    private static HttpResponseMessage Resposta(object conteudo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new { message = new { role = "assistant", content = conteudo is string s ? s : JsonSerializer.Serialize(conteudo) }, done = true }));

    private static readonly PedidoAnaliseFrase Pedido = new(
        "Break a leg tonight!".Split(' '), "Boa sorte hoje à noite!", "en", "pt", ["The show starts at eight."], []);

    [Fact]
    public async Task Envia_frase_traducao_e_contexto_com_esquema_e_le_blocos()
    {
        var handler = new FakeHttpHandler((_, _) => Resposta(new
        {
            sentido = "Desejo de boa sorte antes de uma apresentação.",
            blocos = new object[]
            {
                new { origem = "Break a leg", traducao = "Boa sorte", tipo = "expressao", nota = "Não é literal: deseja boa sorte." },
                new { origem = "tonight!", traducao = "hoje à noite!", tipo = "literal", nota = "" },
            },
        }));

        var analise = await Criar(handler).AnalisarAsync(Pedido, CancellationToken.None);

        Assert.Equal("Desejo de boa sorte antes de uma apresentação.", analise.Sentido);
        Assert.Equal(2, analise.Blocos.Count);
        Assert.Equal(TipoBloco.Expressao, analise.Blocos[0].Tipo);
        Assert.Equal((0, 2), (analise.Blocos[0].PalavraInicial, analise.Blocos[0].PalavraFinal));

        using var doc = JsonDocument.Parse(handler.Recebidas[0].Corpo);
        Assert.Equal("gemma3:12b", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.TryGetProperty("format", out _));
        var usuario = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"frase\":\"Break a leg tonight!\"", usuario, StringComparison.Ordinal);
        Assert.Contains("\"traducao\":\"Boa sorte hoje à noite!\"", usuario, StringComparison.Ordinal);
        Assert.Contains("The show starts at eight.", usuario, StringComparison.Ordinal);
        Assert.Contains("Brazilian Portuguese speaker", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resposta_fora_do_esquema_deixa_a_frase_sem_analise()
    {
        var analise = await Criar(new FakeHttpHandler((_, _) => Resposta("{\"sentido\": \"cortado"))).AnalisarAsync(Pedido, CancellationToken.None);

        Assert.Null(analise.Sentido);
        Assert.Empty(analise.Blocos);
    }

    [Fact]
    public async Task Desligado_nao_verifica_o_modelo()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("não deveria chamar"));
        var analisador = Criar(handler, ativo: false);

        Assert.False(analisador.Ativo);
        await analisador.VerificarAsync(CancellationToken.None);
        Assert.Empty(handler.Recebidas);
    }
}

/// <summary>Analisador falso: um bloco cobrindo a frase e um sentido fixo.</summary>
internal sealed class FakeAnalisador(bool ativo = true) : IAnalisadorSentido
{
    public List<PedidoAnaliseFrase> Pedidos { get; } = [];

    public string Identificador => "fake-analise";

    public bool Ativo => ativo;

    public Task<AnaliseFrase> AnalisarAsync(PedidoAnaliseFrase pedido, CancellationToken cancellationToken)
    {
        Pedidos.Add(pedido);
        return Task.FromResult(new AnaliseFrase(
            "sentido de: " + pedido.Traducao,
            [new Bloco(0, pedido.Palavras.Count - 1, string.Join(' ', pedido.Palavras), pedido.Traducao, TipoBloco.Expressao, "nota")]));
    }
}

public sealed class TranscritorArquivoAnaliseTests : IDisposable
{
    private static readonly TranscricaoBruta Bruta = new("en", 4.0, [],
    [
        new Palavra(" Break", 0, 0.3), new Palavra(" a", 0.3, 0.4), new Palavra(" leg.", 0.4, 0.8),
        new Palavra(" Good", 1.0, 1.3), new Palavra(" luck.", 1.3, 1.7),
    ]);

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private TranscritorArquivo Criar(FakeAnalisador? analisador, out FakeMotor motor)
    {
        var opcoes = Opcoes.Arquivo(o => o.PastaCache = Path.Combine(_pasta.Caminho, "cache"));
        motor = new FakeMotor(Bruta);
        return new TranscritorArquivo(new FakeConversor(new InfoMidia(4, "mp3", 1)), motor, new CacheEmDisco(opcoes), opcoes, new FakeTradutor(), analisador);
    }

    [Fact]
    public async Task Analisa_cada_frase_depois_da_traducao_com_contexto()
    {
        var analisador = new FakeAnalisador();
        var progresso = new ProgressoSincrono();

        var r = await Criar(analisador, out _).ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ambos, progresso);

        Assert.Equal(2, analisador.Pedidos.Count);
        Assert.Equal(["Break", "a", "leg."], analisador.Pedidos[0].Palavras);
        Assert.Equal("[en>pt] Break a leg.", analisador.Pedidos[0].Traducao);
        Assert.Equal(("en", "pt"), (analisador.Pedidos[0].Origem, analisador.Pedidos[0].Destino));
        Assert.Equal(["Good luck."], analisador.Pedidos[0].Seguintes);
        Assert.Equal(["Break a leg."], analisador.Pedidos[1].Anteriores);
        Assert.Equal("sentido de: [en>pt] Break a leg.", r.Frases[0].Sentido);
        Assert.Equal(TipoBloco.Expressao, Assert.Single(r.Frases[0].Blocos!).Tipo);
        Assert.Contains(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.AnalisandoSentido && p.Percentual == 100);

        var json = await File.ReadAllTextAsync(OpcoesArquivo.CaminhoJsonAoLado(_pasta.Caminho + "/a.mp3"));
        Assert.Contains("\"tipo\": \"expressao\"", json, StringComparison.Ordinal);
        Assert.Contains("\"palavraInicial\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"sentido\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segunda_vez_usa_o_cache_da_analise()
    {
        var midia = _pasta.Arquivo("a.mp3", "x");
        await Criar(new FakeAnalisador(), out _).ProcessarAsync(midia, Saida.Ambos);

        var analisador = new FakeAnalisador();
        var progresso = new ProgressoSincrono();
        var r = await Criar(analisador, out var motor).ProcessarAsync(midia, Saida.Ambos, progresso);

        Assert.Empty(analisador.Pedidos);
        Assert.Equal(0, motor.Chamadas);
        Assert.True(progresso.Recebidos[^1].DoCache);
        Assert.NotNull(r.Frases[0].Blocos);
    }

    [Fact]
    public async Task Desligado_ou_sem_traducao_nao_analisa()
    {
        var desligado = new FakeAnalisador(ativo: false);
        var r1 = await Criar(desligado, out _).ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ambos);

        var ligado = new FakeAnalisador();
        var r2 = await Criar(ligado, out _).ProcessarAsync(_pasta.Arquivo("b.mp3", "y"), Saida.Ingles);

        Assert.Empty(desligado.Pedidos);
        Assert.Null(r1.Frases[0].Blocos);
        Assert.Empty(ligado.Pedidos);
        Assert.Null(r2.Frases[0].Sentido);

        var json = JsonTranscricao.Serializar(r1);
        Assert.DoesNotContain("blocos", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sentido", json, StringComparison.Ordinal);
    }
}
