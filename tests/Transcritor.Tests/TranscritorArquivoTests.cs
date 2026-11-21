using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class TranscritorArquivoTests : IDisposable
{
    private static readonly TranscricaoBruta Bruta = new("en", 4.0,
        [new SegmentoBruto(12.4, 15.9, " The mind behind Linux")],
        [
            new Palavra(" The", 12.4, 12.6), new Palavra(" mind", 12.6, 13.0),
            new Palavra(" behind", 13.0, 13.5), new Palavra(" Linux", 13.5, 15.9),
        ]);

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private (TranscritorArquivo Transcritor, FakeConversor Conversor, FakeMotor Motor) Criar(
        InfoMidia? info = null, Action<OpcoesArquivo>? ajustar = null, FakeMotor? motor = null)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            ajustar?.Invoke(o);
        });
        var conversor = new FakeConversor(info ?? new InfoMidia(1834.2, "mp4", 1));
        motor ??= new FakeMotor(Bruta);
        return (new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes), conversor, motor);
    }

    [Fact]
    public async Task Gera_resultado_no_formato_do_contrato()
    {
        var (transcritor, _, motor) = Criar();
        var midia = _pasta.Arquivo("aula.mp4", "conteudo");

        var r = await transcritor.ProcessarAsync(midia);

        Assert.Equal(64, r.Arquivo.Hash.Length);
        Assert.Equal(1834.2, r.Arquivo.Duracao);
        Assert.Equal("en", r.Arquivo.Idioma);
        var frase = Assert.Single(r.Frases);
        Assert.Equal("The mind behind Linux", frase.Texto);
        Assert.Equal(4, frase.Palavras.Count);
        Assert.True(motor.WavExistiaNaChamada);

        var json = await File.ReadAllTextAsync(OpcoesArquivo.CaminhoJsonAoLado(midia));
        Assert.Contains("\"arquivo\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frases\"", json, StringComparison.Ordinal);
        Assert.Contains("\"palavras\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inicio\": 12.4", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"traducao\"", json, StringComparison.Ordinal);
        Assert.Equal(r.Arquivo, JsonTranscricao.Desserializar(json).Arquivo);
    }

    [Fact]
    public async Task Segunda_vez_usa_cache_e_nao_chama_servicos()
    {
        var (transcritor, conversor, motor) = Criar();
        var midia = _pasta.Arquivo("aula.mp4", "conteudo");
        await transcritor.ProcessarAsync(midia);

        var progresso = new ProgressoSincrono();
        var (outro, conversor2, motor2) = Criar();
        var r = await outro.ProcessarAsync(midia, progresso);

        Assert.Equal(1, motor.Chamadas);
        Assert.Equal(0, motor2.Chamadas);
        Assert.Equal(0, conversor2.ChamadasInfo);
        Assert.Equal(0, conversor2.ChamadasConversao);
        Assert.True(progresso.Recebidos[^1].DoCache);
        Assert.Equal("The mind behind Linux", r.Frases[0].Texto);
    }

    [Fact]
    public async Task Outro_modelo_nao_reaproveita_cache()
    {
        var midia = _pasta.Arquivo("aula.mp4", "conteudo");
        await Criar().Transcritor.ProcessarAsync(midia);

        var (transcritor, _, motor) = Criar(motor: new FakeMotor(Bruta, "fake:outro"));
        await transcritor.ProcessarAsync(midia);

        Assert.Equal(1, motor.Chamadas);
    }

    [Fact]
    public async Task Arquivo_diferente_tem_hash_diferente()
    {
        var (transcritor, _, motor) = Criar();
        var a = await transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "um"));
        var b = await transcritor.ProcessarAsync(_pasta.Arquivo("b.mp3", "dois"));

        Assert.NotEqual(a.Arquivo.Hash, b.Arquivo.Hash);
        Assert.Equal(2, motor.Chamadas);
    }

    [Fact]
    public async Task Repassa_idioma_forcado_ao_motor()
    {
        var (transcritor, _, motor) = Criar(ajustar: o => o.Idioma = "pt");
        await transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "x"));

        Assert.Equal("pt", motor.UltimoIdioma);
    }

    [Fact]
    public async Task Reporta_progresso_das_etapas_e_apaga_wav()
    {
        var (transcritor, conversor, _) = Criar(info: new InfoMidia(10, "mp3", 1));
        var progresso = new ProgressoSincrono();

        await transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), progresso);

        var etapas = progresso.Recebidos.Select(p => p.Etapa).Distinct().ToList();
        Assert.Equal(
            [
                EtapaProcessamento.CalculandoHash, EtapaProcessamento.LendoCache, EtapaProcessamento.LendoMidia,
                EtapaProcessamento.ConvertendoAudio, EtapaProcessamento.Transcrevendo, EtapaProcessamento.MontandoFrases,
                EtapaProcessamento.GravandoResultado, EtapaProcessamento.Concluido,
            ],
            etapas);
        Assert.Contains(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.ConvertendoAudio && p.Percentual == 50);
        Assert.NotNull(conversor.UltimoWav);
        Assert.False(File.Exists(conversor.UltimoWav));
    }

    [Fact]
    public async Task Midia_sem_audio_gera_erro()
    {
        var (transcritor, _, motor) = Criar(info: new InfoMidia(10, "mp4", 0));

        await Assert.ThrowsAsync<TranscritorException>(() => transcritor.ProcessarAsync(_pasta.Arquivo("v.mp4", "x")));
        Assert.Equal(0, motor.Chamadas);
    }

    [Fact]
    public async Task Arquivo_inexistente_gera_FileNotFound()
    {
        var (transcritor, _, _) = Criar();
        await Assert.ThrowsAsync<FileNotFoundException>(() => transcritor.ProcessarAsync(Path.Combine(_pasta.Caminho, "nada.mp4")));
    }

    [Fact]
    public async Task Respeita_cancelamento()
    {
        var (transcritor, _, motor) = Criar();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcritor.ProcessarAsync(_pasta.Arquivo("a.mp3", "x"), null, cts.Token));
        Assert.Equal(0, motor.Chamadas);
    }
}
