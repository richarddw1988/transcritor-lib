using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.Core.Gpu;
using Transcritor.Core.Processos;
using Transcritor.TempoReal;
using Transcritor.TempoReal.WhisperLiveKit;

namespace Transcritor.Tests;

public sealed class GerenciadorTempoRealTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private string Trava => Path.Combine(_pasta.Caminho, "gpu.lock");

    /// <summary>Docker falso com o estado do contêiner: start liga, stop desliga.</summary>
    private sealed class DockerFalso
    {
        private readonly object _trava = new();

        public DockerFalso(bool noAr) => NoAr = noAr;

        public bool NoAr { get; private set; }

        public List<string> Comandos { get; } = [];

        public FakeExecutorProcesso Executor => new((_, args, _) =>
        {
            lock (_trava)
            {
                Comandos.Add(args[0]);
                switch (args[0])
                {
                    case "start": NoAr = true; break;
                    case "stop": NoAr = false; break;
                }

                return new ResultadoProcesso(0, args[0] == "inspect" ? (NoAr ? "true\n" : "false\n") : "", "");
            }
        });

        public int Quantos(string comando)
        {
            lock (_trava)
            {
                return Comandos.Count(c => c == comando);
            }
        }
    }

    private GerenciadorTempoReal Criar(DockerFalso docker, Action<OpcoesTempoReal>? ajustar = null, Func<CancellationToken, Task>? verificar = null)
    {
        var o = new OpcoesTempoReal { ArquivoTravaGpu = Trava, DesligarAposOcioso = TimeSpan.FromMinutes(5) };
        ajustar?.Invoke(o);
        var motor = new MotorWhisperLiveKit(new FabricaFake(new FakeConexaoWlk([])), Options.Create(o));
        return new GerenciadorTempoReal(docker.Executor, motor, Options.Create(o))
        {
            VerificarServico = verificar ?? (_ => Task.CompletedTask),
            IntervaloEspera = TimeSpan.FromMilliseconds(20),
        };
    }

    [Fact]
    public async Task Liga_o_conteiner_parado_e_espera_aceitar_conexao()
    {
        var docker = new DockerFalso(noAr: false);
        var tentativas = 0;
        var avisos = new List<string>();
        await using var gerenciador = Criar(docker, verificar: _ =>
            ++tentativas < 3 ? throw new ServicoIndisponivelException("ainda carregando") : Task.CompletedTask);

        await using (await gerenciador.AbrirSessaoAsync(avisos.Add, CancellationToken.None))
        {
            Assert.True(docker.NoAr);
            Assert.Equal(3, tentativas);
            Assert.Contains(avisos, a => a.Contains("Ligando", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Conteiner_no_ar_e_segunda_sessao_nao_ligam_de_novo()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker);

        var a = await gerenciador.AbrirSessaoAsync(null, CancellationToken.None);
        var b = await gerenciador.AbrirSessaoAsync(null, CancellationToken.None);
        await a.DisposeAsync();
        await b.DisposeAsync();

        Assert.Equal(0, docker.Quantos("start"));
        Assert.Equal(1, docker.Quantos("inspect"));
    }

    [Fact]
    public async Task Desliga_depois_do_tempo_ocioso()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(docker, o => o.DesligarAposOcioso = TimeSpan.FromMilliseconds(50));

        await (await gerenciador.AbrirSessaoAsync(null, CancellationToken.None)).DisposeAsync();
        Assert.True(docker.NoAr);

        await EsperarAsync(() => !docker.NoAr);
        Assert.Equal(1, docker.Quantos("stop"));
    }

    [Fact]
    public async Task Sessao_aberta_antes_do_tempo_ocioso_cancela_o_desligamento()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(docker, o => o.DesligarAposOcioso = TimeSpan.FromMilliseconds(150));

        await (await gerenciador.AbrirSessaoAsync(null, CancellationToken.None)).DisposeAsync();
        await using var outra = await gerenciador.AbrirSessaoAsync(null, CancellationToken.None);
        await Task.Delay(400);

        Assert.True(docker.NoAr);
        Assert.Equal(0, docker.Quantos("stop"));
    }

    [Fact]
    public async Task Descartar_faz_na_hora_o_desligamento_pendente()
    {
        var docker = new DockerFalso(noAr: false);
        var gerenciador = Criar(docker);

        await (await gerenciador.AbrirSessaoAsync(null, CancellationToken.None)).DisposeAsync();
        Assert.True(docker.NoAr);

        await gerenciador.DisposeAsync();
        Assert.False(docker.NoAr);
    }

    [Fact]
    public async Task Sessao_tem_prioridade_e_o_arquivo_espera_ela_fechar()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker);

        var sessao = await gerenciador.AbrirSessaoAsync(null, CancellationToken.None);
        var arquivo = VezGpu.PegarAsync(Trava, "arquivo", prioridade: false, null, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        await Task.Delay(150);
        Assert.False(arquivo.IsCompleted);
        Assert.StartsWith("transcritor-lib (tempo real)", CoordenadorGpuTests.Dono(Trava), StringComparison.Ordinal);

        await sessao.DisposeAsync(); // a vez sai na hora; o contêiner só desliga depois do tempo ocioso
        await using var vez = await arquivo.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(docker.NoAr);
    }

    [Fact]
    public async Task Sessao_espera_a_etapa_do_arquivo_e_avisa()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker);
        var etapa = await VezGpu.PegarAsync(Trava, "transcritor-lib (arquivo), processo 1", prioridade: false, null, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        var avisos = new List<string>();

        var abrindo = gerenciador.AbrirSessaoAsync(avisos.Add, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(abrindo.IsCompleted);
        Assert.Contains(avisos, a => a.Contains("Esperando a vez na GPU: transcritor-lib (arquivo)", StringComparison.Ordinal));

        await etapa.DisposeAsync();
        await using var sessao = await abrindo.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Conteiner_que_nao_aceita_conexao_da_erro_e_solta_a_vez()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(
            docker,
            o => o.TempoMaximoSubida = TimeSpan.FromMilliseconds(200),
            _ => throw new ServicoIndisponivelException("não responde"));

        await Assert.ThrowsAsync<ServicoIndisponivelException>(() => gerenciador.AbrirSessaoAsync(null, CancellationToken.None));

        await using var vez = await VezGpu.PegarAsync(Trava, "arquivo", prioridade: false, null, TimeSpan.FromMilliseconds(20), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Sem_gerenciar_nao_mexe_no_docker()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(docker, o => o.GerenciarConteiner = false);

        await (await gerenciador.AbrirSessaoAsync(null, CancellationToken.None)).DisposeAsync();

        Assert.Empty(docker.Comandos);
        Assert.Null(await gerenciador.ConteinerNoArAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transcritor_emite_aguardando_antes_de_ouvir()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker);
        var etapa = await VezGpu.PegarAsync(Trava, "transcritor-lib (arquivo), processo 1", prioridade: false, null, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        var motor = new FakeMotorTempoReal(new EventoTempoReal(TipoEvento.Confirmado, 1, "Oi.", null, "pt", 0, 1));
        var transcritor = new TranscritorTempoReal(motor, Options.Create(new OpcoesTempoReal()), gerenciador: gerenciador);

        var eventos = new List<EventoTempoReal>();
        var lendo = Task.Run(async () =>
        {
            await foreach (var e in transcritor.IniciarAsync(new FakeFonte(3), Saida.Portugues))
            {
                eventos.Add(e);
            }
        });
        await Task.Delay(150);
        Assert.Equal(0, motor.BlocosLidos);
        await etapa.DisposeAsync();
        await lendo.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TipoEvento.Aguardando, eventos[0].Tipo);
        Assert.Equal(TipoEvento.Confirmado, eventos[^1].Tipo);
        Assert.Equal(3, motor.BlocosLidos);
    }

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        for (var i = 0; i < 250 && !condicao(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condicao());
    }
}
