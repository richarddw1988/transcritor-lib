using System.Buffers.Binary;
using System.Net;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Arquivo.Separacao;
using Transcritor.Core;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class SeparadorVozTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static SeparadorDemucs Criar(FakeHttpHandler handler, double segundosPorParte = 10) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://separador.test:8002/") },
            Opcoes.Arquivo(o => o.SegundosPorParteSeparacao = segundosPorParte));

    private static int AmostrasDoWav(byte[] wav) => (int)(RecorteWav.LerChunks(wav).TamanhoDados / 2);

    [Fact]
    public async Task Manda_o_audio_em_partes_e_emenda_as_vozes()
    {
        // 25 s em partes de 10 s: 3 partes; cada voz volta com 1 s (16000 amostras) para conferir a emenda.
        var handler = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ReparadorTrechosTests.Wav(16000)),
        });
        var entrada = Path.Combine(_pasta.Caminho, "filme44.wav");
        await File.WriteAllBytesAsync(entrada, ReparadorTrechosTests.Wav(16000 * 25));
        var saida = Path.Combine(_pasta.Caminho, "voz.wav");
        var avancos = new List<double>();

        await Criar(handler).SepararAsync(entrada, saida, avancos.Add, CancellationToken.None);

        Assert.Equal(3, handler.Recebidas.Count);
        Assert.All(handler.Recebidas, r => Assert.EndsWith("/separar", r.Requisicao.RequestUri!.AbsolutePath, StringComparison.Ordinal));
        var voz = await File.ReadAllBytesAsync(saida);
        Assert.Equal(3 * 16000, AmostrasDoWav(voz));
        Assert.Equal(voz.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(voz.AsSpan(4)));
        Assert.Equal(100, avancos[^1]);
        Assert.False(File.Exists(saida + ".parte.wav"));
    }

    [Fact]
    public async Task Servico_fora_do_ar_diz_como_subir_ou_desligar()
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("recusada"));
        var entrada = Path.Combine(_pasta.Caminho, "a.wav");
        await File.WriteAllBytesAsync(entrada, ReparadorTrechosTests.Wav(16000));

        var erro = await Assert.ThrowsAsync<ServicoIndisponivelException>(
            () => Criar(handler).SepararAsync(entrada, Path.Combine(_pasta.Caminho, "v.wav"), null, CancellationToken.None));

        Assert.Contains("docker compose up -d separador", erro.Message, StringComparison.Ordinal);
        Assert.Contains("SepararVoz", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erro_do_servico_traz_o_motivo()
    {
        var handler = new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("OutOfMemoryError: CUDA out of memory"),
        });
        var entrada = Path.Combine(_pasta.Caminho, "a.wav");
        await File.WriteAllBytesAsync(entrada, ReparadorTrechosTests.Wav(16000));

        var erro = await Assert.ThrowsAsync<TranscritorException>(
            () => Criar(handler).SepararAsync(entrada, Path.Combine(_pasta.Caminho, "v.wav"), null, CancellationToken.None));

        Assert.Contains("CUDA out of memory", erro.Message, StringComparison.Ordinal);
    }

    /// <summary>Separador falso: copia a entrada (precisa ser WAV) e conta as chamadas.</summary>
    private sealed class FakeSeparador : ISeparadorVoz
    {
        public int Chamadas { get; private set; }

        public string Identificador => "fake-separador";

        public Task SepararAsync(string wavEntrada, string wavSaida, Action<double>? aoAvancar, CancellationToken cancellationToken)
        {
            Chamadas++;
            File.Copy(wavEntrada, wavSaida, overwrite: true);
            aoAvancar?.Invoke(100);
            return Task.CompletedTask;
        }
    }

    private (TranscritorArquivo Transcritor, FakeConversor Conversor, FakeSeparador Separador) Montar(
        string layout, ModoSeparacaoVoz modo = ModoSeparacaoVoz.Filmes)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.Idioma = "en";
            o.SepararVoz = modo;
        });
        var conversor = new FakeConversor(new InfoMidia(1, "matroska", 1, ["en"], [layout]), ReparadorTrechosTests.Wav(16000));
        var separador = new FakeSeparador();
        var motor = new FakeMotor(new TranscricaoBruta("en", 1, [new SegmentoBruto(0, 1, "Hi.")], [new Palavra("Hi.", 0, 1)]));
        return (new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes, separador: separador), conversor, separador);
    }

    [Fact]
    public async Task Filme_com_canal_central_passa_pelo_separador_em_44_khz()
    {
        var (transcritor, conversor, separador) = Montar("5.1");
        var progresso = new ProgressoSincrono();

        await transcritor.ProcessarAsync(_pasta.Arquivo("filme.mkv", "x"), Saida.Ingles, progresso);

        Assert.Equal(1, separador.Chamadas);
        Assert.Equal(44100, conversor.UltimaTaxa);
        Assert.Contains(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.SeparandoVoz);
        Assert.Contains(".sv-Filmes-fake-separador", transcritor.ChaveCache("abc"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stereo", ModoSeparacaoVoz.Filmes)]
    [InlineData("", ModoSeparacaoVoz.Filmes)]
    [InlineData("5.1", ModoSeparacaoVoz.Nunca)]
    public async Task Estereo_ou_separacao_desligada_nao_passam_pelo_separador(string layout, ModoSeparacaoVoz modo)
    {
        var (transcritor, conversor, separador) = Montar(layout, modo);

        await transcritor.ProcessarAsync(_pasta.Arquivo("a.mkv", "x"), Saida.Ingles);

        Assert.Equal(0, separador.Chamadas);
        Assert.Equal(16000, conversor.UltimaTaxa);
        Assert.Equal(modo != ModoSeparacaoVoz.Nunca, transcritor.ChaveCache("abc").Contains(".sv-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sempre_separa_ate_estereo()
    {
        var (transcritor, _, separador) = Montar("stereo", ModoSeparacaoVoz.Sempre);

        await transcritor.ProcessarAsync(_pasta.Arquivo("aula.mp4", "x"), Saida.Ingles);

        Assert.Equal(1, separador.Chamadas);
    }

    [Fact]
    public void Ffprobe_informa_o_layout_de_cada_faixa()
    {
        const string json = """
            { "format": { "duration": "10.0", "format_name": "matroska,webm" },
              "streams": [
                { "codec_type": "video" },
                { "codec_type": "audio", "channel_layout": "5.1(side)", "tags": { "language": "por" } },
                { "codec_type": "audio", "channel_layout": "stereo", "tags": { "language": "eng" } },
                { "codec_type": "audio" } ] }
            """;

        var info = ConversorFfmpeg.InterpretarFfprobe(json);

        Assert.Equal(["5.1(side)", "stereo", ""], info.LayoutsFaixas);
        Assert.True(CanaisAudio.TemCanalCentral(info.LayoutsFaixas![0]));
        Assert.False(CanaisAudio.TemCanalCentral(info.LayoutsFaixas[1]));
    }

    [Fact]
    public async Task Separador_descarrega_e_verifica_pelo_health()
    {
        var handler = new FakeHttpHandler((r, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var separador = Criar(handler);

        await separador.VerificarAsync(CancellationToken.None);
        await separador.DescarregarAsync(CancellationToken.None);

        Assert.Equal(["/health", "/descarregar"], handler.Recebidas.Select(r => r.Requisicao.RequestUri!.AbsolutePath));
    }
}
