using System.Buffers.Binary;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Arquivo.Separacao;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class LacunasTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private const double Alta = 1e8;   // ~−10 dB: voz
    private const double Baixa = 1e3;  // ~−60 dB: silêncio

    /// <summary>Energia de 0,1 em 0,1 s: baixa em tudo, alta nos intervalos dados.</summary>
    private static double[] Energia(double duracao, params (double De, double Ate)[] voz)
    {
        var e = Enumerable.Repeat(Baixa, (int)Math.Round(duracao / PedacosAudio.PassoEnergia)).ToArray();
        foreach (var (de, ate) in voz)
        {
            for (var k = (int)Math.Round(de / PedacosAudio.PassoEnergia); k < (int)Math.Round(ate / PedacosAudio.PassoEnergia); k++)
            {
                e[k] = Alta;
            }
        }

        return e;
    }

    [Fact]
    public void Acha_voz_sem_palavra_e_ignora_voz_coberta_e_estalo_curto()
    {
        var energia = Energia(20, (2, 3), (5, 6), (10, 10.2), (14, 14.4), (14.6, 15.2));
        var palavras = new[] { new Palavra(" Hi.", 5.1, 5.8) };

        var lacunas = Lacunas.Detectar(energia, palavras);

        // 2–3 s: lacuna; 5–6 s: coberta pela palavra; 10–10,2 s: curta demais; 14–15,2 s: um trecho só (buraco de 0,2 s).
        Assert.Equal([(2.0, 3.0), (14.0, 15.2)], lacunas);
    }

    private static TranscricaoBruta Nova(string texto, double semFala) =>
        new("en", 2, [new SegmentoBruto(0, 2, " " + texto, semFala)],
            texto.Split(' ').Select((w, i) => new Palavra(" " + w, 0.5 + (i * 0.3), 0.7 + (i * 0.3))).ToList());

    [Fact]
    public void Aceita_fala_e_poe_no_tempo_do_arquivo()
    {
        var aceita = Lacunas.Aceitar(Nova("You yield.", 0.04), inicioRecorte: 100, inicio: 100.5, fim: 101.5);

        Assert.NotNull(aceita);
        Assert.Equal([100.5, 100.8], aceita.Palavras.Select(p => p.Inicio));
        Assert.Equal(" You yield.", Assert.Single(aceita.Segmentos).Texto);
    }

    [Theory]
    [InlineData("Thank you for watching.", 0.05)]
    [InlineData("Thank you for", 0.05)]
    [InlineData("you for watching!", 0.05)]
    [InlineData("Thank you.", 0.05)]
    [InlineData("Where are you?", 0.25)]
    public void Recusa_frase_inventada_e_trecho_que_parece_silencio(string texto, double semFala)
    {
        Assert.Null(Lacunas.Aceitar(Nova(texto, semFala), 0, 0, 3));
    }

    [Fact]
    public void Recusa_palavra_fora_da_lacuna_e_motor_que_nao_informa_silencio()
    {
        Assert.Null(Lacunas.Aceitar(Nova("No!", 0.05), inicioRecorte: 0, inicio: 5, fim: 6));
        var semInfo = Nova("No!", 0.05) with { Segmentos = [new SegmentoBruto(0, 1, " No!")] };
        Assert.Null(Lacunas.Aceitar(semInfo, 0, 0, 3));
    }

    /// <summary>WAV 16 kHz mono de 10 s com voz (onda forte) entre 4 e 5 s.</summary>
    private static byte[] WavComVoz()
    {
        var wav = ReparadorTrechosTests.Wav(16000 * 10);
        wav.AsSpan(44).Clear();
        for (var i = 16000 * 4; i < 16000 * 5; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (i * 2)), (short)(i % 2 == 0 ? 10000 : -10000));
        }

        return wav;
    }

    private sealed class SeparadorQueCopia : ISeparadorVoz
    {
        public string Identificador => "copia";

        public Task SepararAsync(string wavEntrada, string wavSaida, Action<double>? aoAvancar, CancellationToken cancellationToken)
        {
            File.Copy(wavEntrada, wavSaida, overwrite: true);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ModoSeparacaoVoz.Filmes, "5.1", true)]
    [InlineData(ModoSeparacaoVoz.Filmes, "stereo", false)]
    public async Task So_com_a_voz_separada_a_lacuna_e_transcrita_de_novo(ModoSeparacaoVoz modo, string layout, bool esperaLacuna)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.Idioma = "en";
            o.SepararVoz = modo;
            o.RepararTrechosSuspeitos = false;
        });
        // 1ª chamada: o arquivo inteiro, com uma palavra só no começo; 2ª: a lacuna de 4–5 s.
        var motor = new FakeMotorSequencia(
            new TranscricaoBruta("en", 10, [new SegmentoBruto(0, 1, " Hi.", 0.01)], [new Palavra(" Hi.", 0.2, 0.6)]),
            Nova("No!", 0.05));
        var conversor = new FakeConversor(new InfoMidia(10, "matroska", 1, ["en"], [layout]), WavComVoz());
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes, separador: new SeparadorQueCopia());

        var r = await transcritor.ProcessarAsync(_pasta.Arquivo("filme.mkv", "x"), Saida.Ingles);

        var palavras = r.Frases.SelectMany(f => f.Palavras).Select(p => p.Texto.Trim()).ToList();
        Assert.Equal(esperaLacuna ? ["Hi.", "No!"] : ["Hi."], palavras);
        Assert.Equal(esperaLacuna ? 2 : 1, motor.TamanhosWav.Count);
    }
}
