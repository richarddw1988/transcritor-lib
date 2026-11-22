using System.Net;
using System.Text.Json;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Arquivo.Speaches;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;
using Transcritor.Traducao;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

/// <summary>Cada etapa do arquivo tira o seu modelo da GPU antes da próxima; a correção roda em duas fases.</summary>
public sealed class DescarregarModelosTests : IDisposable
{
    private static readonly TranscricaoBruta Bruta = new("en", 4.0, [],
    [
        new Palavra(" The", 0, 0.3, 0.99), new Palavra(" night", 0.3, 0.6, 0.51), new Palavra(" rode.", 0.6, 0.9, 0.98),
        new Palavra(" Good", 1.0, 1.3, 0.99), new Palavra(" night.", 1.3, 1.7, 0.4),
    ]);

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    [Fact]
    public async Task Propostas_de_todas_as_frases_vem_antes_da_conferencia_e_cada_etapa_descarrega()
    {
        var eventos = new List<string>();
        var r = await Criar(eventos, descarregar: true).ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ambos);

        Assert.Equal(
            [
                "transcrever", "descarregar:motor",
                "propor", "propor", "descarregar:corretor",
                "reouvir", "reouvir", "descarregar:motor",
                "traduzir", "descarregar:tradutor",
            ],
            eventos);
        Assert.Equal(["The knight rode.", "Good knight."], r.Frases.Select(f => f.Texto));
    }

    [Fact]
    public async Task Desligado_nao_descarrega_e_a_ordem_das_etapas_e_a_mesma()
    {
        var eventos = new List<string>();
        await Criar(eventos, descarregar: false).ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ambos);

        Assert.Equal(["transcrever", "propor", "propor", "reouvir", "reouvir", "traduzir"], eventos);
    }

    [Fact]
    public async Task Tudo_do_cache_nao_descarrega_nada()
    {
        var midia = _pasta.Arquivo("a.mp3", "x");
        await Criar([], descarregar: true).ProcessarAsync(midia, Saida.Ambos);

        var eventos = new List<string>();
        await Criar(eventos, descarregar: true).ProcessarAsync(midia, Saida.Ambos);

        Assert.Empty(eventos);
    }

    [Fact]
    public async Task Ollama_descarrega_com_keep_alive_zero_e_ignora_falha()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"done": true, "done_reason": "unload"}"""));
        await CriarTradutor(handler).DescarregarAsync(CancellationToken.None);

        var (requisicao, corpo) = Assert.Single(handler.Recebidas);
        Assert.Equal("/api/generate", requisicao.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(corpo);
        Assert.Equal("translategemma:12b", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("keep_alive").GetInt32());

        var fora = new FakeHttpHandler((_, _) => throw new HttpRequestException("recusado"));
        await CriarTradutor(fora).DescarregarAsync(CancellationToken.None);
        var erro = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{}", HttpStatusCode.InternalServerError));
        await CriarTradutor(erro).DescarregarAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Speaches_espera_o_whisper_sair_da_lista_de_carregados()
    {
        var respostas = new Queue<string>([
            """{"models": ["Systran/faster-whisper-large-v3"]}""",
            """{"models": ["Systran/faster-whisper-large-v3"]}""",
            """{"models": []}""",
        ]);
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(respostas.Dequeue()));

        await CriarSpeaches(handler, TimeSpan.FromSeconds(10)).DescarregarAsync(CancellationToken.None);

        Assert.Equal(3, handler.Recebidas.Count);
        Assert.All(handler.Recebidas, r => Assert.Equal((HttpMethod.Get, "/api/ps"), (r.Requisicao.Method, r.Requisicao.RequestUri!.AbsolutePath)));
    }

    [Fact]
    public async Task Speaches_desiste_depois_da_espera_maxima_e_ignora_servico_fora()
    {
        var carregado = new FakeHttpHandler((_, _) => FakeHttpHandler.Json("""{"models": ["Systran/faster-whisper-large-v3"]}"""));
        await CriarSpeaches(carregado, TimeSpan.FromMilliseconds(50)).DescarregarAsync(CancellationToken.None);
        Assert.NotEmpty(carregado.Recebidas);

        var fora = new FakeHttpHandler((_, _) => throw new HttpRequestException("recusado"));
        await CriarSpeaches(fora, TimeSpan.FromSeconds(10)).DescarregarAsync(CancellationToken.None);
        Assert.Single(fora.Recebidas);
    }

    private TranscritorArquivo Criar(List<string> eventos, bool descarregar)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.DescarregarModelosEntreEtapas = descarregar;
        });
        var conversor = new FakeConversor(new InfoMidia(4, "mp3", 1), ReparadorTrechosTests.Wav(16000 * 4));
        return new TranscritorArquivo(
            conversor, new MotorRegistrado(eventos), new CacheEmDisco(opcoes), opcoes, new TradutorRegistrado(eventos),
            corretor: new CorretorRegistrado(eventos));
    }

    private static TradutorOllama CriarTradutor(FakeHttpHandler handler)
    {
        var opcoes = Microsoft.Extensions.Options.Options.Create(new OpcoesTraducao { OllamaUrl = new Uri("http://ollama.test:11434") });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return new TradutorOllama(new ClienteOllama(http, opcoes), new CachePalavrasEmDisco(opcoes), opcoes);
    }

    private static SpeachesMotorTranscricao CriarSpeaches(FakeHttpHandler handler, TimeSpan espera)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.EsperaDescarregarTranscricao = espera;
            o.IntervaloConsultaDescarregar = TimeSpan.FromMilliseconds(10);
        });
        return new SpeachesMotorTranscricao(new HttpClient(handler) { BaseAddress = new Uri("http://speaches.test:8000/") }, opcoes);
    }

    /// <summary>Transcreve o arquivo (sem contexto) ou reouve a frase (com a versão trocada como contexto, ouvida com confiança 0,5).</summary>
    private sealed class MotorRegistrado(List<string> eventos) : IMotorTranscricao, IDescarregavel
    {
        public string Identificador => "fake:registrado";

        public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, CancellationToken cancellationToken) =>
            TranscreverAsync(caminhoWav, idioma, null, cancellationToken);

        public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, string? contexto, CancellationToken cancellationToken)
        {
            if (contexto is null)
            {
                eventos.Add("transcrever");
                return Task.FromResult(Bruta);
            }

            eventos.Add("reouvir");
            var palavras = contexto.Split(' ').Select((p, i) => new Palavra(" " + p, 0.3 + (i * 0.2), 0.5 + (i * 0.2), 0.5)).ToList();
            return Task.FromResult(new TranscricaoBruta("en", 1.5, [], palavras));
        }

        public Task DescarregarAsync(CancellationToken cancellationToken)
        {
            eventos.Add("descarregar:motor");
            return Task.CompletedTask;
        }
    }

    private sealed class CorretorRegistrado(List<string> eventos) : ICorretorTranscricao, IDescarregavel
    {
        public string Identificador => "fake-correcao-registrada";

        public bool Ativo => true;

        public Task<IReadOnlyList<Correcao>> CorrigirAsync(PedidoCorrecao pedido, CancellationToken cancellationToken)
        {
            eventos.Add("propor");
            IReadOnlyList<Correcao> trocas = pedido.Palavras
                .Select((p, i) => p.TrimEnd('.') == "night" ? new Correcao(i, p, p.Replace("night", "knight", StringComparison.Ordinal)) : null)
                .OfType<Correcao>()
                .ToList();
            return Task.FromResult(trocas);
        }

        public Task DescarregarAsync(CancellationToken cancellationToken)
        {
            eventos.Add("descarregar:corretor");
            return Task.CompletedTask;
        }
    }

    private sealed class TradutorRegistrado(List<string> eventos) : ITradutor, IDescarregavel
    {
        private readonly FakeTradutor _fake = new("fake-tradutor-registrado");

        public string Identificador => _fake.Identificador;

        public Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken) =>
            _fake.TraduzirFraseAsync(pedido, cancellationToken);

        public Task<IReadOnlyList<string>> TraduzirFrasesAsync(
            IReadOnlyList<string> frases, string origem, string destino, IProgress<double>? progresso, CancellationToken cancellationToken)
        {
            eventos.Add("traduzir");
            return _fake.TraduzirFrasesAsync(frases, origem, destino, progresso, cancellationToken);
        }

        public Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken) =>
            _fake.TraduzirPalavraAsync(pedido, cancellationToken);

        public Task DescarregarAsync(CancellationToken cancellationToken)
        {
            eventos.Add("descarregar:tradutor");
            return Task.CompletedTask;
        }
    }
}
