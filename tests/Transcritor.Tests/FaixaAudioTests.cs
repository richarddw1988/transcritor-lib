using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core.Processos;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

/// <summary>Filme dublado: escolher a faixa de áudio do idioma pedido.</summary>
public sealed class FaixaAudioTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static readonly InfoMidia Dublado = new(6968, "matroska,webm", 2, ["pt", "en"]);

    [Theory]
    [InlineData("en", 1)]
    [InlineData("EN", 1)]
    [InlineData("pt", 0)]
    [InlineData(null, 0)]
    public void Escolhe_a_faixa_do_idioma_forcado(string? idioma, int faixa)
    {
        Assert.Equal(faixa, TranscritorArquivo.EscolherFaixa(Dublado, idioma));
    }

    [Fact]
    public void Sem_faixa_do_idioma_ou_sem_idiomas_fica_a_primeira()
    {
        Assert.Equal(0, TranscritorArquivo.EscolherFaixa(new InfoMidia(1, "mkv", 2, ["pt", "es"]), "en"));
        Assert.Equal(0, TranscritorArquivo.EscolherFaixa(new InfoMidia(1, "mkv", 2), "en"));
        Assert.Equal(0, TranscritorArquivo.EscolherFaixa(new InfoMidia(1, "mp3", 1, ["en"]), "en"));
    }

    [Fact]
    public void Le_o_idioma_de_cada_faixa_de_audio()
    {
        var info = ConversorFfmpeg.InterpretarFfprobe("""
            {
              "streams": [
                { "codec_type": "video" },
                { "codec_type": "audio", "tags": { "language": "por", "title": "Portuguese (Brazil)" } },
                { "codec_type": "audio", "tags": { "language": "eng" } },
                { "codec_type": "audio", "tags": { "language": "und" } },
                { "codec_type": "audio" },
                { "codec_type": "subtitle", "tags": { "language": "eng" } }
              ],
              "format": { "format_name": "matroska,webm", "duration": "6968.36" }
            }
            """);

        Assert.Equal(4, info.FaixasAudio);
        Assert.Equal(["pt", "en", "", ""], info.IdiomasFaixas);
    }

    [Fact]
    public async Task Converte_a_faixa_pedida()
    {
        var executor = new FakeExecutorProcesso((_, _, _) => new ResultadoProcesso(0, "", ""));
        var conversor = new ConversorFfmpeg(executor, Opcoes.Arquivo());

        await conversor.ConverterParaWavAsync("filme.mkv", "out.wav", 1, null, CancellationToken.None);

        var args = executor.Chamadas[0].Argumentos.ToList();
        Assert.Equal("0:a:1", args[args.IndexOf("-map") + 1]);
    }

    [Fact]
    public async Task Filme_dublado_com_idioma_ingles_transcreve_a_faixa_em_ingles()
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.Idioma = "en";
        });
        var conversor = new FakeConversor(Dublado);
        var motor = new FakeMotor(new TranscricaoBruta("en", 1, [new SegmentoBruto(0, 1, "Hi.")], [new Palavra("Hi.", 0, 1)]));
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes);

        await transcritor.ProcessarAsync(_pasta.Arquivo("filme.mkv", "x"), Saida.Ingles);

        Assert.Equal(1, conversor.UltimaFaixa);
        Assert.Equal("en", motor.UltimoIdioma);
    }

    [Fact]
    public void Codigos_de_tres_letras_viram_pt_e_en()
    {
        Assert.Equal("pt", Idiomas.Normalizar("por"));
        Assert.Equal("en", Idiomas.Normalizar("ENG"));
        Assert.Equal("spa", Idiomas.Normalizar("spa"));
    }
}
