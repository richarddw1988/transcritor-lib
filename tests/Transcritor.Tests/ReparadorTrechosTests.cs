using System.Buffers.Binary;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class ReparadorTrechosTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    /// <summary>
    /// Uma palavra por segundo (0,6 s de fala, 0,4 s de pausa), todas com vírgula; depois de "p3" e de "p16" a pausa
    /// é de 1,4 s.
    /// </summary>
    private static List<Palavra> Normais(int quantidade)
    {
        var lista = new List<Palavra>();
        var t = 0.0;
        for (var i = 0; i < quantidade; i++)
        {
            lista.Add(new Palavra($" p{i},", Math.Round(t, 3), Math.Round(t + 0.6, 3)));
            t += i is 3 or 16 ? 2.0 : 1.0;
        }

        return lista;
    }

    /// <summary>Normais com uma repetição alucinada (4 palavras de duração zero) no meio, como no audiolivro.</summary>
    private static List<Palavra> ComAlucinacao()
    {
        var p = Normais(20);
        var t = p[9].Fim;
        p.InsertRange(10, [
            new Palavra(" And", t, t), new Palavra(" I'm", t, t), new Palavra(" going", t, t), new Palavra(" to", t, t),
        ]);
        return p;
    }

    [Fact]
    public void Detecta_repeticao_com_duracao_zero_e_corta_nas_pausas_em_volta()
    {
        var palavras = ComAlucinacao();

        var janela = Assert.Single(ReparadorTrechos.Detectar(palavras, 30));

        // Maior pausa antes: entre p3 (fim 3,6) e p4 (início 5,0); depois: entre p16 (fim 17,6) e p17 (início 19,0).
        Assert.Equal(4.3, janela.Inicio);
        Assert.Equal((17.6 + 19.0) / 2, janela.Fim, 3);
    }

    [Fact]
    public void Uma_ou_duas_palavras_curtas_nao_sao_trecho_suspeito()
    {
        var palavras = Normais(20);
        palavras[5] = palavras[5] with { Fim = palavras[5].Inicio };
        palavras[12] = palavras[12] with { Fim = palavras[12].Inicio };

        Assert.Empty(ReparadorTrechos.Detectar(palavras, 30));
    }

    [Fact]
    public void Trecho_no_comeco_corta_no_zero()
    {
        var palavras = Normais(10);
        for (var i = 0; i < 3; i++)
        {
            palavras[i] = palavras[i] with { Fim = palavras[i].Inicio };
        }

        Assert.Equal(0, Assert.Single(ReparadorTrechos.Detectar(palavras, 12)).Inicio);
    }

    [Fact]
    public void Substituir_troca_so_a_janela_e_desloca_os_tempos()
    {
        var original = new TranscricaoBruta("en", 30,
            [new SegmentoBruto(0, 4, " antes"), new SegmentoBruto(4.5, 18, " meio"), new SegmentoBruto(19, 30, " depois")],
            ComAlucinacao());
        var janela = new ReparadorTrechos.Janela(4.3, 18.3);
        var nova = new TranscricaoBruta("en", 13, [new SegmentoBruto(0.2, 12.8, " novo")],
            [new Palavra(" sipping", 0.7, 1.2), new Palavra(" tall", 1.2, 1.5), new Palavra(" glasses", 1.5, 2.1)]);

        Assert.True(ReparadorTrechos.Melhorou(original, janela, nova));
        var r = ReparadorTrechos.Substituir(original, janela, nova);

        Assert.Equal(
            [" p0,", " p1,", " p2,", " p3,", " sipping", " tall", " glasses", " p17,", " p18,", " p19,"],
            r.Palavras.Select(p => p.Texto));
        Assert.Equal(5.0, r.Palavras[4].Inicio);
        Assert.Equal(6.4, r.Palavras[6].Fim);
        Assert.DoesNotContain(r.Palavras, ReparadorTrechos.Suspeita);
        Assert.Equal([" antes", " novo", " depois"], r.Segmentos.Select(s => s.Texto));
        Assert.Equal(4.5, r.Segmentos[1].Inicio);
    }

    [Fact]
    public void Resultado_novo_vazio_ou_igualmente_ruim_nao_entra()
    {
        var original = new TranscricaoBruta("en", 30, [], ComAlucinacao());
        var janela = new ReparadorTrechos.Janela(4.3, 18.3);

        Assert.False(ReparadorTrechos.Melhorou(original, janela, new TranscricaoBruta("en", 13, [], [])));
        var ruim = new TranscricaoBruta("en", 13, [],
            [new Palavra(" a", 1, 1), new Palavra(" b", 1, 1), new Palavra(" c", 1, 1), new Palavra(" d", 1, 1)]);
        Assert.False(ReparadorTrechos.Melhorou(original, janela, ruim));
    }

    [Fact]
    public void Quinze_segundos_sem_nenhuma_pontuacao_viram_trecho_suspeito()
    {
        // p0..p3 com vírgula; p4..p22 sem pontuação (18 s); p23 fecha com ponto; depois normal.
        var palavras = Normais(30).Select((p, i) => i is >= 4 and <= 22 ? p with { Texto = $" q{i}" } : p).ToList();

        var janela = Assert.Single(ReparadorTrechos.Detectar(palavras, 40));

        Assert.True(janela.Inicio <= palavras[4].Inicio);
        Assert.True(janela.Fim >= palavras[23].Fim);
        Assert.Empty(ReparadorTrechos.Detectar(Normais(30), 40));
    }

    [Fact]
    public void Trecho_sem_pontuacao_aceita_resultado_novo_pontuado_e_recusa_igual()
    {
        var semPontuacao = Normais(30).Select((p, i) => i is >= 4 and <= 22 ? p with { Texto = $" q{i}" } : p).ToList();
        var original = new TranscricaoBruta("en", 40, [], semPontuacao);
        var janela = ReparadorTrechos.Detectar(semPontuacao, 40)[0];
        var largura = janela.Fim - janela.Inicio;
        var pontuada = new TranscricaoBruta("en", largura, [],
            [.. Enumerable.Range(0, 20).Select(i => new Palavra(i % 4 == 3 ? " fim." : " w", i, i + 0.6))]);
        var igual = new TranscricaoBruta("en", largura, [],
            [.. Enumerable.Range(0, 25).Select(i => new Palavra(" w", i, i + 0.6))]);

        Assert.True(ReparadorTrechos.Melhorou(original, janela, pontuada));
        Assert.False(ReparadorTrechos.Melhorou(original, janela, igual));
    }

    [Fact]
    public void Troca_que_perde_a_pontuacao_nao_entra_mesmo_com_menos_palavras_sem_som()
    {
        // Visto no audiolivro: o recorte sem contexto saiu "he cried one thousand long slimy crocodile tongues..."
        var original = new TranscricaoBruta("en", 30, [], ComAlucinacao());
        var janela = new ReparadorTrechos.Janela(4.3, 18.3);
        var semPontuacao = new TranscricaoBruta("en", 20, [],
            [.. Enumerable.Range(0, 20).Select(i => new Palavra(" w", i, i + 0.6))]);

        Assert.False(ReparadorTrechos.Melhorou(original, janela, semPontuacao));
    }

    [Fact]
    public void Contexto_sao_as_ultimas_palavras_boas_antes_da_janela()
    {
        var palavras = Normais(60);
        palavras[50] = palavras[50] with { Fim = palavras[50].Inicio };

        var contexto = ReparadorTrechos.ContextoAntes(palavras, new ReparadorTrechos.Janela(palavras[55].Inicio - 0.1, 70));

        Assert.StartsWith("p14,", contexto, StringComparison.Ordinal);
        Assert.EndsWith("p49, p51, p52, p53, p54,", contexto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recorte_do_wav_copia_so_as_amostras_do_intervalo()
    {
        // 2 s de áudio 16 kHz mono s16le, cada amostra = índice/16 (dá para conferir de onde veio o recorte),
        // com um chunk LIST antes do data, como o ffmpeg grava.
        var origem = Path.Combine(_pasta.Caminho, "a.wav");
        await File.WriteAllBytesAsync(origem, Wav(32000, comList: true));
        var destino = Path.Combine(_pasta.Caminho, "b.wav");

        await RecorteWav.RecortarAsync(origem, destino, 0.5, 1.0, CancellationToken.None);

        var b = await File.ReadAllBytesAsync(destino);
        Assert.Equal(44 + (8000 * 2), b.Length);
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24)));
        Assert.Equal(8000 * 2, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(40)));
        Assert.Equal((short)(8000 / 16), BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(44)));
    }

    [Fact]
    public async Task Recorte_de_arquivo_que_nao_e_wav_da_erro_de_dados()
    {
        var origem = Path.Combine(_pasta.Caminho, "x.wav");
        await File.WriteAllBytesAsync(origem, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RecorteWav.RecortarAsync(origem, Path.Combine(_pasta.Caminho, "y.wav"), 0, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Pipeline_refaz_o_trecho_isolado_no_idioma_detectado_e_usa_o_resultado()
    {
        var bruta = new TranscricaoBruta("en", 30, [new SegmentoBruto(0, 30, " x")], ComAlucinacao());
        var trecho = new TranscricaoBruta("en", 13, [],
            [new Palavra(" sipping", 0.7, 1.2), new Palavra(" tall", 1.2, 1.5), new Palavra(" glasses.", 1.5, 2.1)]);
        var motor = new FakeMotorSequencia(bruta, trecho);
        var opcoes = Opcoes.Arquivo(o => o.PastaCache = Path.Combine(_pasta.Caminho, "cache"));
        var conversor = new FakeConversor(new InfoMidia(30, "wav", 1), Wav(16000 * 30));
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes);

        var r = await transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ingles);

        Assert.Equal(2, motor.TamanhosWav.Count);
        Assert.InRange(motor.TamanhosWav[1], 44 + ((18.3 - 4.3) * 32000) - 4, 44 + ((18.3 - 4.3) * 32000) + 4);
        Assert.Equal([null, "en"], motor.Idiomas);
        Assert.Equal([null, "p0, p1, p2, p3,"], motor.Contextos);
        var palavras = r.Frases.SelectMany(f => f.Palavras).Select(p => p.Texto).ToList();
        Assert.Contains("sipping", palavras);
        Assert.DoesNotContain("going", palavras);
    }

    [Fact]
    public async Task Reparo_desligado_nao_chama_o_motor_de_novo_e_muda_a_chave_do_cache()
    {
        var bruta = new TranscricaoBruta("en", 30, [], ComAlucinacao());
        var motor = new FakeMotorSequencia(bruta);
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.RepararTrechosSuspeitos = false;
        });
        var transcritor = new TranscritorArquivo(
            new FakeConversor(new InfoMidia(30, "wav", 1), Wav(16000 * 30)), motor, new CacheEmDisco(opcoes), opcoes);

        await transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), Saida.Ingles);

        Assert.Single(motor.TamanhosWav);
        Assert.DoesNotContain(".r", transcritor.ChaveCache("abc"), StringComparison.Ordinal);
    }

    internal static byte[] Wav(int amostras, bool comList = false)
    {
        var list = comList ? 26 : 0;
        var b = new byte[44 + list + (amostras * 2)];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length - 8);
        "WAVE"u8.CopyTo(b.AsSpan(8));
        "fmt "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 16);
        var pos = 36;
        if (comList)
        {
            "LIST"u8.CopyTo(b.AsSpan(pos));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pos + 4), 18);
            pos += 26;
        }

        "data"u8.CopyTo(b.AsSpan(pos));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pos + 4), amostras * 2);
        for (var i = 0; i < amostras; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(pos + 8 + (i * 2)), (short)(i / 16));
        }

        return b;
    }
}
