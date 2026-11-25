using Transcritor.Arquivo.Midia;
using Transcritor.Core.Processos;
using Transcritor.Core.Erros;

namespace Transcritor.Tests;

public class ConversorFfmpegTests
{
    private const string SaidaFfprobe = """
        {
          "streams": [ { "index": 0, "codec_type": "video" }, { "index": 1, "codec_type": "audio" } ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "1834.200000" }
        }
        """;

    [Fact]
    public async Task Le_duracao_formato_e_faixas()
    {
        var executor = new FakeExecutorProcesso((_, _, _) => new ResultadoProcesso(0, SaidaFfprobe, ""));
        var conversor = new ConversorFfmpeg(executor, Opcoes.Arquivo(o => o.CaminhoFfprobe = "C:/ff/ffprobe.exe"));

        var info = await conversor.ObterInfoAsync("aula.mp4", CancellationToken.None);

        Assert.Equal(1834.2, info.Duracao);
        Assert.Equal(1, info.FaixasAudio);
        Assert.StartsWith("mov,mp4", info.Formato, StringComparison.Ordinal);
        Assert.Equal("C:/ff/ffprobe.exe", executor.Chamadas[0].Executavel);
        Assert.Equal("aula.mp4", executor.Chamadas[0].Argumentos[^1]);
    }

    [Fact]
    public void Prefere_duracao_da_faixa_de_audio_a_do_conteiner()
    {
        var info = ConversorFfmpeg.InterpretarFfprobe("""
            {
              "streams": [ { "codec_type": "video", "duration": "16.40" }, { "codec_type": "audio", "duration": "14.102" } ],
              "format": { "format_name": "mp4", "duration": "16.400000" }
            }
            """);

        Assert.Equal(14.102, info.Duracao);
    }

    [Fact]
    public async Task Converte_para_wav_16k_mono_e_reporta_progresso()
    {
        var executor = new FakeExecutorProcesso((_, _, aoLerLinha) =>
        {
            aoLerLinha?.Invoke("frame=0");
            aoLerLinha?.Invoke("out_time_us=5000000");
            aoLerLinha?.Invoke("out_time_us=N/A");
            aoLerLinha?.Invoke("progress=end");
            return new ResultadoProcesso(0, "", "");
        });
        var conversor = new ConversorFfmpeg(executor, Opcoes.Arquivo());
        var avancos = new List<double>();

        await conversor.ConverterParaWavAsync("in.mp3", "out.wav", avancos.Add, CancellationToken.None);

        var args = executor.Chamadas[^1].Argumentos;
        Assert.Equal("ffmpeg", executor.Chamadas[^1].Executavel);
        Assert.Equal("16000", args[args.ToList().IndexOf("-ar") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("-ac") + 1]);
        Assert.Equal("pcm_s16le", args[args.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal("in.mp3", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("out.wav", args[^1]);
        Assert.Equal([5.0], avancos);
    }

    [Theory]
    [InlineData("5.1 ", true, true)]
    [InlineData("7.1", true, true)]
    [InlineData("stereo", true, false)]
    [InlineData("mono", true, false)]
    [InlineData("", true, false)]
    [InlineData("5.1", false, false)]
    public async Task Faixa_com_canal_central_vira_so_o_canal_central(string layout, bool opcao, bool esperaCentral)
    {
        var executor = new FakeExecutorProcesso((exe, _, _) => new ResultadoProcesso(0, exe == "ffprobe" ? layout : "", ""));
        var conversor = new ConversorFfmpeg(executor, Opcoes.Arquivo(o => o.UsarCanalCentral = opcao));

        await conversor.ConverterParaWavAsync("filme.mkv", "out.wav", null, CancellationToken.None);

        var args = executor.Chamadas[^1].Argumentos.ToList();
        Assert.Equal(esperaCentral, args.Contains("pan=mono|c0=FC"));
        Assert.Equal(!esperaCentral, args.Contains("-ac"));
        Assert.Equal(opcao ? 2 : 1, executor.Chamadas.Count);
    }

    [Fact]
    public async Task Falha_do_ffmpeg_vira_erro_com_stderr()
    {
        var executor = new FakeExecutorProcesso((_, _, _) => new ResultadoProcesso(1, "", "in.xyz: Invalid data found"));
        var conversor = new ConversorFfmpeg(executor, Opcoes.Arquivo());

        var ex = await Assert.ThrowsAsync<FerramentaExternaException>(
            () => conversor.ConverterParaWavAsync("in.xyz", "out.wav", null, CancellationToken.None));
        Assert.Contains("Invalid data found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executavel_inexistente_gera_erro_claro()
    {
        var conversor = new ConversorFfmpeg(new ExecutorProcesso(), Opcoes.Arquivo(o => o.CaminhoFfprobe = "nao-existe-ffprobe-123"));

        var ex = await Assert.ThrowsAsync<FerramentaExternaException>(
            () => conversor.ObterInfoAsync("x.mp4", CancellationToken.None));
        Assert.Contains("nao-existe-ffprobe-123", ex.Message, StringComparison.Ordinal);
    }
}
