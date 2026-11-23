using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class ConferenciaIdiomaTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    /// <summary>Transcrição de uma palavra com a confiança dada.</summary>
    private static TranscricaoBruta Bruta(string idioma, string texto, double? confianca) =>
        new(idioma, 4, [new SegmentoBruto(0, 1, texto)], [new Palavra(texto, 0, 1, confianca)]);

    /// <summary>Responde conforme o idioma pedido (nulo = detecção) e guarda o idioma e o tamanho de cada WAV.</summary>
    private sealed class MotorPorIdioma(TranscricaoBruta detectada, TranscricaoBruta? ingles = null, TranscricaoBruta? portugues = null)
        : IMotorTranscricao
    {
        public List<string?> Idiomas { get; } = [];

        public List<long> TamanhosWav { get; } = [];

        public string Identificador => "fake:idioma";

        public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, CancellationToken cancellationToken)
        {
            Idiomas.Add(idioma);
            TamanhosWav.Add(new FileInfo(caminhoWav).Length);
            return Task.FromResult(idioma switch
            {
                "en" => ingles ?? detectada,
                "pt" => portugues ?? detectada,
                _ => detectada,
            });
        }
    }

    private async Task<(ResultadoTranscricao Resultado, MotorPorIdioma Motor)> Transcrever(
        MotorPorIdioma motor, double duracao = 8, Action<OpcoesArquivo>? ajustar = null)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.RepararTrechosSuspeitos = false;
            ajustar?.Invoke(o);
        });
        var conversor = new FakeConversor(new InfoMidia(duracao, "wav", 1), ReparadorTrechosTests.Wav((int)(16000 * duracao)));
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes, new FakeTradutor());
        var r = await transcritor.ProcessarAsync(_pasta.Arquivo("fala.wav", "x"), Saida.Ingles);
        return (r, motor);
    }

    [Fact]
    public async Task Deteccao_errada_vira_o_outro_idioma_sem_transcrever_de_novo_o_arquivo_curto()
    {
        var motor = new MotorPorIdioma(
            Bruta("pt", "Eu posso descobrir.", 0.64), ingles: Bruta("en", "I can find out.", 0.9));

        var (r, _) = await Transcrever(motor);

        Assert.Equal("en", r.Arquivo.Idioma);
        Assert.Equal("I can find out.", r.Frases[0].Texto);
        Assert.Equal([null, "en"], motor.Idiomas);
    }

    [Fact]
    public async Task Deteccao_certa_fica_e_so_custa_a_amostra_no_outro_idioma()
    {
        var motor = new MotorPorIdioma(
            Bruta("en", "The knight rode.", 0.85), portugues: Bruta("pt", "O cavaleiro.", 0.74));

        var (r, _) = await Transcrever(motor);

        Assert.Equal("en", r.Arquivo.Idioma);
        Assert.Equal("The knight rode.", r.Frases[0].Texto);
        Assert.Equal([null, "pt"], motor.Idiomas);
    }

    [Fact]
    public async Task Rotulo_errado_com_texto_certo_tambem_e_corrigido()
    {
        // en07: o Whisper disse "pt" mas escreveu em inglês; forçado em inglês, as palavras saem com um pouco mais de confiança.
        var motor = new MotorPorIdioma(
            Bruta("pt", "It was quiet.", 0.845), ingles: Bruta("en", "It was quiet.", 0.847));

        var (r, _) = await Transcrever(motor);

        Assert.Equal("en", r.Arquivo.Idioma);
    }

    [Fact]
    public async Task Arquivo_longo_confere_na_amostra_e_transcreve_inteiro_no_idioma_vencedor()
    {
        var motor = new MotorPorIdioma(
            Bruta("pt", "Eu posso.", 0.6), ingles: Bruta("en", "I can.", 0.9));

        var (r, _) = await Transcrever(motor, duracao: 70);

        Assert.Equal("en", r.Arquivo.Idioma);
        Assert.Equal([null, "en", "en"], motor.Idiomas);
        var amostra = 44 + (16000 * 30 * 2);
        Assert.Equal([amostra, amostra, 44 + (16000 * 70 * 2)], motor.TamanhosWav);
    }

    [Fact]
    public async Task Arquivo_longo_com_deteccao_certa_forca_o_idioma_detectado()
    {
        var motor = new MotorPorIdioma(Bruta("en", "I can.", 0.9), portugues: Bruta("pt", "Eu posso.", 0.6));

        await Transcrever(motor, duracao: 70);

        Assert.Equal([null, "pt", "en"], motor.Idiomas);
    }

    [Fact]
    public async Task Idioma_forcado_nao_confere()
    {
        var motor = new MotorPorIdioma(Bruta("pt", "Eu posso.", 0.6), ingles: Bruta("en", "I can.", 0.9));

        var (r, _) = await Transcrever(motor, ajustar: o => o.Idioma = "pt");

        Assert.Equal([Idiomas.Portugues], motor.Idiomas);
        Assert.Equal("pt", r.Arquivo.Idioma);
    }

    [Fact]
    public async Task Desligado_usa_so_a_deteccao()
    {
        var motor = new MotorPorIdioma(Bruta("pt", "Eu posso.", 0.6), ingles: Bruta("en", "I can.", 0.9));

        var (r, _) = await Transcrever(motor, ajustar: o => o.ConferirIdioma = false);

        Assert.Equal([null], motor.Idiomas);
        Assert.Equal("pt", r.Arquivo.Idioma);
    }

    [Theory]
    [InlineData("es", 0.9)]
    [InlineData("en", null)]
    public async Task Outro_idioma_ou_motor_sem_confianca_fica_com_a_deteccao(string detectado, double? confianca)
    {
        var motor = new MotorPorIdioma(Bruta(detectado, "Hola.", confianca), portugues: Bruta("pt", "Olá.", 0.99));

        var (r, _) = await Transcrever(motor);

        Assert.Equal([null], motor.Idiomas);
        Assert.Equal(detectado, r.Arquivo.Idioma);
    }

    [Fact]
    public void Chave_do_cache_muda_com_a_conferencia()
    {
        TranscritorArquivo Com(Action<OpcoesArquivo> ajustar)
        {
            var opcoes = Opcoes.Arquivo(ajustar);
            return new TranscritorArquivo(
                new FakeConversor(new InfoMidia(1, "wav", 1)), new FakeMotor(Bruta("en", "x", null)), new CacheEmDisco(opcoes), opcoes);
        }

        Assert.Contains(".auto-c1.", Com(_ => { }).ChaveCache("abc"), StringComparison.Ordinal);
        Assert.Contains(".auto.", Com(o => o.ConferirIdioma = false).ChaveCache("abc"), StringComparison.Ordinal);
        Assert.Contains(".en.", Com(o => o.Idioma = "en").ChaveCache("abc"), StringComparison.Ordinal);
    }

    [Fact]
    public void Media_da_confianca_ignora_palavras_sem_confianca()
    {
        var t = new TranscricaoBruta("en", 1, [], [new Palavra("a", 0, 1, 0.5), new Palavra("b", 0, 1), new Palavra("c", 0, 1, 1.0)]);

        Assert.Equal(0.75, ConferenciaIdioma.ConfiancaMedia(t));
        Assert.Null(ConferenciaIdioma.ConfiancaMedia(t with { Palavras = [new Palavra("a", 0, 1)] }));
    }
}
