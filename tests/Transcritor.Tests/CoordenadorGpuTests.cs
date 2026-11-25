using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Arquivo.Processos;
using Transcritor.Core;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class CoordenadorGpuTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    /// <summary>Docker falso: o contêiner do tempo real está no ar ou não.</summary>
    private static FakeExecutorProcesso Docker(bool tempoRealNoAr) =>
        new((_, args, _) => args[0] == "inspect"
            ? new ResultadoProcesso(0, tempoRealNoAr ? "true\n" : "false\n", "")
            : new ResultadoProcesso(0, "", ""));

    private CoordenadorGpu Criar(FakeExecutorProcesso executor, Action<OpcoesArquivo>? ajustar = null) =>
        new(executor, Opcoes.Arquivo(o =>
        {
            o.ArquivoTravaGpu = Path.Combine(_pasta.Caminho, "gpu.lock");
            ajustar?.Invoke(o);
        }))
        {
            IntervaloEspera = TimeSpan.FromMilliseconds(20),
        };

    private static List<string> Comandos(FakeExecutorProcesso executor) =>
        executor.Chamadas.Select(c => string.Join(' ', c.Argumentos)).ToList();

    [Fact]
    public async Task Segunda_reserva_espera_a_primeira_soltar_e_diz_quem_esta_com_a_vez()
    {
        var primeiro = Criar(Docker(false));
        var segundo = Criar(Docker(false));
        string? dono = null;

        var reserva = await primeiro.ReservarAsync(null, CancellationToken.None);
        var esperando = segundo.ReservarAsync(d => dono = d, CancellationToken.None);
        await Task.Delay(200);

        Assert.False(esperando.IsCompleted);
        Assert.Contains("transcritor-lib", dono, StringComparison.Ordinal);

        await reserva.DisposeAsync();
        await using var segunda = await esperando.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Espera_pode_ser_cancelada()
    {
        await using var reserva = await Criar(Docker(false)).ReservarAsync(null, CancellationToken.None);
        using var cancelar = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Criar(Docker(false)).ReservarAsync(null, cancelar.Token));
    }

    [Fact]
    public async Task Para_o_tempo_real_no_ar_e_religa_ao_soltar()
    {
        var docker = Docker(tempoRealNoAr: true);

        var reserva = await Criar(docker).ReservarAsync(null, CancellationToken.None);
        Assert.Equal(["inspect -f {{.State.Running}} whisperlivekit", "stop whisperlivekit"], Comandos(docker));

        await reserva.DisposeAsync();
        await reserva.DisposeAsync(); // soltar duas vezes não religa duas vezes
        Assert.Equal("start whisperlivekit", Comandos(docker)[^1]);
        Assert.Equal(3, docker.Chamadas.Count);
    }

    [Fact]
    public async Task Tempo_real_parado_nao_e_mexido_nem_religado()
    {
        var docker = Docker(tempoRealNoAr: false);

        await (await Criar(docker).ReservarAsync(null, CancellationToken.None)).DisposeAsync();

        Assert.Equal(["inspect -f {{.State.Running}} whisperlivekit"], Comandos(docker));
    }

    [Fact]
    public async Task Sem_docker_segue_sem_erro_e_opcoes_desligadas_nao_mexem_em_nada()
    {
        var semDocker = new FakeExecutorProcesso((_, _, _) => throw new FerramentaExternaException("docker não encontrado"));
        await (await Criar(semDocker).ReservarAsync(null, CancellationToken.None)).DisposeAsync();

        var docker = Docker(tempoRealNoAr: true);
        await (await Criar(docker, o => { o.PararTempoRealDuranteArquivo = false; o.CoordenarGpu = false; })
            .ReservarAsync(null, CancellationToken.None)).DisposeAsync();
        Assert.Empty(docker.Chamadas);
    }

    [Fact]
    public async Task Arquivo_para_o_tempo_real_so_quando_transcreve_e_nao_quando_vem_do_cache()
    {
        var docker = Docker(tempoRealNoAr: true);
        var opcoes = Opcoes.Arquivo(o => o.PastaCache = Path.Combine(_pasta.Caminho, "cache"));
        var coordenador = Criar(docker);
        var motor = new FakeMotor(new TranscricaoBruta("en", 1, [new SegmentoBruto(0, 1, "Hi.")], [new Palavra("Hi.", 0, 1)]));
        var transcritor = new TranscritorArquivo(
            new FakeConversor(new InfoMidia(1, "wav", 1)), motor, new CacheEmDisco(opcoes), opcoes, coordenador: coordenador);
        var midia = _pasta.Arquivo("a.wav", "x");

        await transcritor.ProcessarAsync(midia, Saida.Ingles);
        Assert.Equal(["inspect -f {{.State.Running}} whisperlivekit", "stop whisperlivekit", "start whisperlivekit"], Comandos(docker));

        await transcritor.ProcessarAsync(midia, Saida.Ingles);
        Assert.Equal(3, docker.Chamadas.Count);
        Assert.Equal(1, motor.Chamadas);
    }

    [Fact]
    public async Task Arquivo_avisa_no_progresso_enquanto_espera_a_vez()
    {
        var opcoes = Opcoes.Arquivo(o => o.PastaCache = Path.Combine(_pasta.Caminho, "cache"));
        var motor = new FakeMotor(new TranscricaoBruta("en", 1, [new SegmentoBruto(0, 1, "Hi.")], [new Palavra("Hi.", 0, 1)]));
        var transcritor = new TranscritorArquivo(
            new FakeConversor(new InfoMidia(1, "wav", 1)), motor, new CacheEmDisco(opcoes), opcoes, coordenador: Criar(Docker(false)));
        var progresso = new ProgressoSincrono();
        var outro = await Criar(Docker(false)).ReservarAsync(null, CancellationToken.None);

        var processando = transcritor.ProcessarAsync(_pasta.Arquivo("a.wav", "x"), Saida.Ingles, progresso);
        await Task.Delay(200);
        Assert.Equal(0, motor.Chamadas);
        await outro.DisposeAsync();
        await processando.WaitAsync(TimeSpan.FromSeconds(5));

        var aviso = Assert.Single(progresso.Recebidos, p => p.Etapa == EtapaProcessamento.AguardandoGpu);
        Assert.Contains("transcritor-lib", aviso.Detalhe, StringComparison.Ordinal);
        Assert.Equal(1, motor.Chamadas);
    }
}
