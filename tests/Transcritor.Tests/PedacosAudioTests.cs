using System.Buffers.Binary;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class PedacosAudioTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    /// <summary>Energia de 0,1 em 0,1 s: alta em tudo, baixa (silêncio) a partir dos segundos dados por 1 s.</summary>
    private static double[] Energia(double duracao, params double[] silencios)
    {
        var e = Enumerable.Repeat(1000.0, (int)(duracao / PedacosAudio.PassoEnergia)).ToArray();
        foreach (var s in silencios)
        {
            for (var p = (int)(s / PedacosAudio.PassoEnergia); p < (int)((s + 1) / PedacosAudio.PassoEnergia); p++)
            {
                e[p] = 0;
            }
        }

        return e;
    }

    [Fact]
    public void Corta_no_silencio_perto_de_cada_marca()
    {
        // Marcas em 600 e 1200 s; silêncios em 585 s e 1219 s (dentro dos 30 s de busca).
        var limites = PedacosAudio.Limites(Energia(1700, 585, 1219), 1700, 600, 30);

        Assert.Equal(3, limites.Count);
        Assert.Equal(0, limites[0].Inicio);
        Assert.InRange(limites[0].Fim, 585, 586);
        Assert.Equal(limites[0].Fim, limites[1].Inicio);
        Assert.InRange(limites[1].Fim, 1219, 1220);
        Assert.Equal(1700, limites[2].Fim);
    }

    [Fact]
    public void Audio_que_cabe_em_um_pedaco_e_meio_vai_inteiro()
    {
        Assert.Equal([(0.0, 900.0)], PedacosAudio.Limites(Energia(900), 900, 600, 30));
        Assert.Equal([(0.0, 5000.0)], PedacosAudio.Limites(Energia(5000), 5000, 0, 30));
    }

    [Fact]
    public void Junta_somando_o_inicio_de_cada_pedaco()
    {
        var a = new TranscricaoBruta("en", 10, [new SegmentoBruto(0, 2, "Hi.")], [new Palavra(" Hi.", 0.5, 1.0, 0.9)]);
        var b = new TranscricaoBruta("en", 10, [new SegmentoBruto(1, 3, "Bye.")], [new Palavra(" Bye.", 1.5, 2.0)]);

        var junta = PedacosAudio.Juntar([(0, 600.5, a), (600.5, 1000, b)], 1000);

        Assert.Equal(1000, junta.Duracao);
        Assert.Equal([0.5, 602.0], junta.Palavras.Select(p => p.Inicio));
        Assert.Equal([601.5], junta.Segmentos.Skip(1).Select(s => s.Inicio));
    }

    [Fact]
    public async Task Le_a_energia_do_wav()
    {
        var wav = ReparadorTrechosTests.Wav(16000);
        wav.AsSpan(44).Clear(); // silêncio
        for (var i = 0; i < 1600; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 2)), 100); // 1º décimo de segundo com som
        }

        var caminho = Path.Combine(_pasta.Caminho, "e.wav");
        await File.WriteAllBytesAsync(caminho, wav);

        var energia = await PedacosAudio.EnergiaAsync(caminho, CancellationToken.None);

        Assert.Equal(10, energia.Length);
        Assert.Equal(10000, energia[0]);
        Assert.Equal(0, energia[1]);
    }

    [Fact]
    public async Task Arquivo_longo_e_transcrito_em_pedacos_com_os_tempos_somados()
    {
        // 40 s de áudio, pedaços de 15 s: 3 pedaços.
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.Idioma = "en";
            o.RepararTrechosSuspeitos = false;
            o.SegundosPorPedaco = 15;
        });
        var motor = new FakeMotorSequencia(new TranscricaoBruta("en", 15, [new SegmentoBruto(0, 1, "Hi.")], [new Palavra(" Hi.", 0.2, 0.8)]));
        var conversor = new FakeConversor(new InfoMidia(40, "wav", 1), ReparadorTrechosTests.Wav(16000 * 40));
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes);

        var r = await transcritor.ProcessarAsync(_pasta.Arquivo("longo.wav", "x"), Saida.Ingles);

        Assert.Equal(3, motor.TamanhosWav.Count);
        Assert.All(motor.Idiomas, i => Assert.Equal("en", i));
        var inicios = r.Frases.SelectMany(f => f.Palavras).Select(p => p.Inicio).ToList();
        Assert.Equal(3, inicios.Count);
        Assert.Equal(0.2, inicios[0]);
        Assert.True(inicios[1] > 10 && inicios[2] > inicios[1] + 10);
        Assert.Contains(".p2-15", transcritor.ChaveCache("abc"), StringComparison.Ordinal);

        // O 1º pedaço vai sem dica; os seguintes levam as últimas palavras do anterior.
        Assert.Equal([null, "Hi.", "Hi."], motor.Contextos);
    }

    [Fact]
    public void Tira_frase_inventada_inteira_e_mantem_frase_real_parecida()
    {
        var bruta = new TranscricaoBruta(
            "en",
            30,
            [new SegmentoBruto(0, 2, " Yeah! Thanks for watching!"), new SegmentoBruto(5, 7, " He was watching.")],
            [
                new Palavra(" Yeah!", 0, 0.5), new Palavra(" Thanks", 0.6, 0.9), new Palavra(" for", 0.9, 1.0),
                new Palavra(" watching!", 1.0, 1.5), new Palavra(" He", 5, 5.2), new Palavra(" was", 5.2, 5.5),
                new Palavra(" watching.", 5.5, 6),
            ]);

        var limpa = PedacosAudio.SemFrasesInventadas(bruta);

        Assert.Equal(["Yeah!", "He", "was", "watching."], limpa.Palavras.Select(p => p.Texto.Trim()));
        var semNada = bruta with { Palavras = limpa.Palavras, Segmentos = [new SegmentoBruto(5, 7, " He was watching.")] };
        Assert.Same(semNada, PedacosAudio.SemFrasesInventadas(semNada));
    }

    [Fact]
    public void Contexto_do_fim_sao_as_ultimas_palavras()
    {
        var palavras = Enumerable.Range(0, 50).Select(i => new Palavra($" p{i}", i, i + 0.5)).ToList();

        var contexto = PedacosAudio.ContextoDoFim(new TranscricaoBruta("en", 60, [], palavras))!;

        Assert.StartsWith("p10 ", contexto, StringComparison.Ordinal);
        Assert.EndsWith(" p49", contexto, StringComparison.Ordinal);
        Assert.Null(PedacosAudio.ContextoDoFim(new TranscricaoBruta("en", 1, [], [])));
    }
}
