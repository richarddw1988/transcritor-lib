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
                "descarregar:corretor", "descarregar:tradutor", "transcrever", "descarregar:motor",
                "descarregar:motor", "propor", "propor", "descarregar:corretor", "descarregar:tradutor",
                "reouvir", "reouvir", "descarregar:motor",
                "descarregar:motor", "traduzir", "descarregar:tradutor",
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
    public async Task Whisper_de_outra_execucao_sai_antes_da_traducao_mesmo_com_a_transcricao_do_cache()
    {
        var midia = _pasta.Arquivo("a.mp3", "x");
        await Criar([], descarregar: true).ProcessarAsync(midia, Saida.Ingles);

        // Transcrição e correção vêm do cache; só a tradução roda.
        var eventos = new List<string>();
        await Criar(eventos, descarregar: true).ProcessarAsync(midia, Saida.Ambos);

        Assert.Equal(["descarregar:motor", "traduzir", "descarregar:tradutor"], eventos);
    }

    [Fact]
    public async Task Lote_roda_cada_etapa_para_todos_e_carrega_cada_modelo_uma_vez()
    {
        var eventos = new List<string>();
        var a = _pasta.Arquivo("a.mp3", "x");
        var b = _pasta.Arquivo("b.mp3", "y");

        var r = await Criar(eventos, descarregar: true).ProcessarLoteAsync([a, b], Saida.Ambos);

        Assert.Equal(
            [
                "descarregar:corretor", "descarregar:tradutor", "transcrever", "transcrever", "descarregar:motor",
                "descarregar:motor", "propor", "propor", "propor", "propor", "descarregar:corretor", "descarregar:tradutor",
                "reouvir", "reouvir", "reouvir", "reouvir", "descarregar:motor",
                "descarregar:motor", "traduzir", "traduzir", "descarregar:tradutor",
            ],
            eventos);
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Equal("The knight rode.", x.Frases[0].Texto));
        Assert.True(File.Exists(OpcoesArquivo.CaminhoJsonAoLado(a)));
        Assert.True(File.Exists(OpcoesArquivo.CaminhoJsonAoLado(b)));
    }

    [Fact]
    public async Task Lote_informa_o_arquivo_no_progresso_e_reaproveita_o_cache_de_cada_um()
    {
        var a = _pasta.Arquivo("a.mp3", "x");
        var b = _pasta.Arquivo("b.mp3", "y");
        await Criar([], descarregar: true).ProcessarAsync(a, Saida.Ambos);

        var eventos = new List<string>();
        var progresso = new ProgressoSincrono();
        await Criar(eventos, descarregar: true).ProcessarLoteAsync([a, b], Saida.Ambos, progresso);

        // Só o 2º arquivo passa pelas etapas; o 1º vem todo do cache.
        Assert.Equal(1, eventos.Count(e => e == "transcrever"));
        Assert.Equal(1, eventos.Count(e => e == "traduzir"));
        var concluidos = progresso.Recebidos.Where(p => p.Etapa == EtapaProcessamento.Concluido).ToList();
        Assert.Equal([(1, 2, true), (2, 2, false)], concluidos.Select(p => (p.Arquivo, p.TotalArquivos, p.DoCache)));
        Assert.Contains(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.Transcrevendo && p.Arquivo == 2);
        Assert.DoesNotContain(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.Transcrevendo && p.Arquivo == 1);
    }

    [Fact]
    public async Task Lote_com_arquivo_ausente_falha_antes_de_chamar_servicos()
    {
        var eventos = new List<string>();
        var ausente = Path.Combine(_pasta.Caminho, "nao.mp3");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Criar(eventos, descarregar: true).ProcessarLoteAsync([_pasta.Arquivo("a.mp3", "x"), ausente], Saida.Ambos));

        Assert.Equal(ausente, ex.FileName);
        Assert.Empty(eventos);
    }

    [Fact]
    public async Task Implementacao_padrao_do_lote_processa_um_arquivo_por_vez()
    {
        ITranscritorArquivo transcritor = new TranscritorUmPorVez();

        var r = await transcritor.ProcessarLoteAsync(["a.mp3", "b.mp3"], Saida.Ingles);

        Assert.Equal(["a.mp3", "b.mp3"], r.Select(x => x.Arquivo.Hash));
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
    public async Task Speaches_da_imagem_do_projeto_descarrega_com_delete()
    {
        var carregado = true;
        var handler = new FakeHttpHandler((req, _) =>
        {
            switch (req.Method.Method, req.RequestUri!.AbsolutePath)
            {
                case ("GET", "/health"):
                    var saude = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
                    saude.Headers.Add(SpeachesMotorTranscricao.CabecalhoDescarregar, "1");
                    return saude;
                case ("DELETE", "/api/ps/Systran/faster-whisper-large-v3"):
                    carregado = false;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return FakeHttpHandler.Json(carregado ? """{"models": ["Systran/faster-whisper-large-v3"]}""" : """{"models": []}""");
            }
        });

        await CriarSpeaches(handler, TimeSpan.FromSeconds(10)).DescarregarAsync(CancellationToken.None);

        Assert.Equal(
            ["GET /health", "DELETE /api/ps/Systran/faster-whisper-large-v3", "GET /api/ps"],
            handler.Recebidas.Select(r => $"{r.Requisicao.Method} {r.Requisicao.RequestUri!.AbsolutePath}"));
    }

    [Fact]
    public async Task Speaches_original_nao_manda_delete_e_espera_o_whisper_sair_da_lista()
    {
        var respostas = new Queue<string>([
            """{"models": ["Systran/faster-whisper-large-v3"]}""",
            """{"models": ["Systran/faster-whisper-large-v3"]}""",
            """{"models": []}""",
        ]);
        var handler = new FakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath == "/health"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") }
            : FakeHttpHandler.Json(respostas.Dequeue()));

        await CriarSpeaches(handler, TimeSpan.FromSeconds(10)).DescarregarAsync(CancellationToken.None);

        Assert.DoesNotContain(handler.Recebidas, r => r.Requisicao.Method == HttpMethod.Delete);
        Assert.Equal(3, handler.Recebidas.Count(r => r.Requisicao.RequestUri!.AbsolutePath == "/api/ps"));
    }

    [Fact]
    public async Task Speaches_desiste_depois_da_espera_maxima_e_ignora_servico_fora()
    {
        var carregado = new FakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath == "/health"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : FakeHttpHandler.Json("""{"models": ["Systran/faster-whisper-large-v3"]}"""));
        await CriarSpeaches(carregado, TimeSpan.FromMilliseconds(50)).DescarregarAsync(CancellationToken.None);
        Assert.Contains(carregado.Recebidas, r => r.Requisicao.RequestUri!.AbsolutePath == "/api/ps");

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
            o.ConferirIdioma = false;
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

    /// <summary>Só implementa <see cref="ITranscritorArquivo.ProcessarAsync"/>; o lote vem da implementação padrão.</summary>
    private sealed class TranscritorUmPorVez : ITranscritorArquivo
    {
        public Task<ResultadoTranscricao> ProcessarAsync(
            string caminhoArquivo, Saida saida, IProgress<ProgressoProcessamento>? progresso = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResultadoTranscricao(new InfoArquivo(caminhoArquivo, 1, "en"), []));
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
