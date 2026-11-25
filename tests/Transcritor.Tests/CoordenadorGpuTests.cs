using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core.Processos;
using Transcritor.Core;
using Transcritor.Core.Erros;
using Transcritor.Core.Gpu;
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

    internal static string Dono(string caminho)
    {
        using var leitura = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var texto = new StreamReader(leitura);
        return texto.ReadLine() ?? "";
    }

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
    public async Task Para_o_tempo_real_ocioso_e_nao_religa_ao_soltar()
    {
        var docker = Docker(tempoRealNoAr: true);

        var reserva = await Criar(docker).ReservarAsync(null, CancellationToken.None);
        Assert.Equal(["inspect -f {{.State.Running}} whisperlivekit", "stop whisperlivekit"], Comandos(docker));

        await reserva.DisposeAsync();
        await reserva.DisposeAsync();
        Assert.Equal(2, docker.Chamadas.Count); // a próxima sessão de tempo real é que liga
    }

    [Fact]
    public async Task Arquivo_cede_a_vez_ao_tempo_real_que_pediu_antes()
    {
        var caminho = Path.Combine(_pasta.Caminho, "gpu.lock");
        var intervalo = TimeSpan.FromMilliseconds(20);
        var arquivo1 = await VezGpu.PegarAsync(caminho, "arquivo 1", prioridade: false, null, intervalo, CancellationToken.None);

        var tempoReal = VezGpu.PegarAsync(caminho, "tempo real", prioridade: true, null, intervalo, CancellationToken.None);
        await Task.Delay(100);
        string? motivo = null;
        var arquivo2 = VezGpu.PegarAsync(caminho, "arquivo 2", prioridade: false, m => motivo = m, intervalo, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(tempoReal.IsCompleted);
        Assert.False(arquivo2.IsCompleted);
        Assert.Contains("tempo real", motivo, StringComparison.Ordinal);

        await arquivo1.DisposeAsync(); // fim da etapa: quem pegou foi o tempo real, não o outro arquivo
        var vezTempoReal = await tempoReal.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(arquivo2.IsCompleted);
        Assert.StartsWith("tempo real", Dono(caminho), StringComparison.Ordinal);

        await vezTempoReal.DisposeAsync();
        await using var vezArquivo2 = await arquivo2.WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.Equal(["inspect -f {{.State.Running}} whisperlivekit", "stop whisperlivekit"], Comandos(docker));

        await transcritor.ProcessarAsync(midia, Saida.Ingles);
        Assert.Equal(2, docker.Chamadas.Count);
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
