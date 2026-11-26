using System.Net;
using Transcritor.Core.Erros;
using Transcritor.Core.Processos;

namespace Transcritor.Tests;

public sealed class GerenciadorConteineresTests : IDisposable
{
    private const string Nome = "speaches";
    private const int OutroPid = 999_999;

    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    /// <summary>Docker falso com o estado do contêiner: start liga, stop desliga.</summary>
    private sealed class DockerFalso(bool noAr)
    {
        private readonly object _trava = new();

        public bool NoAr { get; private set; } = noAr;

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

        public void Parar()
        {
            lock (_trava)
            {
                NoAr = false;
            }
        }

        public int Quantos(string comando)
        {
            lock (_trava)
            {
                return Comandos.Count(c => c == comando);
            }
        }
    }

    private GerenciadorConteineres Criar(DockerFalso docker, TimeSpan? ocioso = null, Action<OpcoesConteineres>? ajustar = null)
    {
        var o = new OpcoesConteineres { PastaUso = _pasta.Caminho, DesligarAposOcioso = ocioso ?? TimeSpan.FromMinutes(5) };
        ajustar?.Invoke(o);
        return new GerenciadorConteineres(docker.Executor, o)
        {
            IntervaloEspera = TimeSpan.FromMilliseconds(10),
            ProcessoVivo = pid => pid == OutroPid,
        };
    }

    private static Func<CancellationToken, Task<bool>> Pronto(DockerFalso docker) => _ => Task.FromResult(docker.NoAr);

    private static async Task Esperar(Func<bool> condicao)
    {
        for (var i = 0; i < 200 && !condicao(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Liga_o_conteiner_parado_e_espera_o_servico_responder()
    {
        var docker = new DockerFalso(noAr: false);
        var respostas = 0;
        var avisos = new List<string>();
        await using var gerenciador = Criar(docker);

        await using (await gerenciador.UsarAsync(Nome, _ => Task.FromResult(++respostas >= 3), avisos.Add, CancellationToken.None))
        {
            Assert.True(docker.NoAr);
            Assert.Equal(3, respostas);
            Assert.Contains(avisos, a => a.Contains("Ligando o contêiner speaches", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Uso_recente_nao_consulta_o_docker_de_novo()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker);

        for (var i = 0; i < 5; i++)
        {
            await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
            {
            }
        }

        Assert.Equal(1, docker.Quantos("inspect"));
        Assert.Equal(0, docker.Quantos("start"));
    }

    [Fact]
    public async Task Desliga_depois_do_tempo_ocioso()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(docker, ocioso: TimeSpan.FromMilliseconds(50));

        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        string[] Marcas() => Directory.GetFiles(_pasta.Caminho).Where(f => !f.EndsWith(".trava", StringComparison.Ordinal)).ToArray();
        await Esperar(() => !docker.NoAr && Marcas().Length == 0);
        Assert.False(docker.NoAr);
        Assert.Empty(Marcas());
    }

    [Fact]
    public async Task Usar_de_novo_antes_do_tempo_ocioso_cancela_o_desligamento()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker, ocioso: TimeSpan.FromMilliseconds(150));

        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
            await Task.Delay(300);
            Assert.True(docker.NoAr); // em uso: não desliga
        }
    }

    [Fact]
    public async Task Nao_desliga_o_que_outro_programa_esta_usando_ou_usou_ha_pouco()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker, ocioso: TimeSpan.FromMilliseconds(50));
        var usoOutro = _pasta.Arquivo($"speaches.{OutroPid}.uso", "");

        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        await Task.Delay(200);
        Assert.True(docker.NoAr);

        // Outro programa terminou há pouco: a marca "último uso" dele segura o contêiner no ar.
        File.Delete(usoOutro);
        var ultimoOutro = _pasta.Arquivo($"speaches.{OutroPid}.ultimo", "");
        File.SetLastWriteTimeUtc(ultimoOutro, DateTime.UtcNow.AddMinutes(10));
        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        await Task.Delay(200);
        Assert.True(docker.NoAr);
        Assert.Equal(0, docker.Quantos("stop"));
    }

    [Fact]
    public async Task Marca_de_programa_que_morreu_nao_segura_o_conteiner()
    {
        var docker = new DockerFalso(noAr: true);
        await using var gerenciador = Criar(docker, ocioso: TimeSpan.FromMilliseconds(50));
        var morto = _pasta.Arquivo("speaches.123456.uso", "");

        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        await Esperar(() => !docker.NoAr);
        Assert.False(docker.NoAr);
        Assert.False(File.Exists(morto));
    }

    [Fact]
    public async Task Ao_descartar_desliga_na_hora_o_que_esperava_o_tempo_ocioso()
    {
        var docker = new DockerFalso(noAr: false);
        var gerenciador = Criar(docker);
        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        Assert.True(docker.NoAr);
        await gerenciador.DisposeAsync();
        Assert.False(docker.NoAr);
    }

    [Fact]
    public async Task Sem_tempo_ocioso_nunca_desliga()
    {
        var docker = new DockerFalso(noAr: false);
        var gerenciador = Criar(docker, ajustar: o => o.DesligarAposOcioso = null);
        await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
        {
        }

        await gerenciador.DisposeAsync();
        Assert.True(docker.NoAr);
    }

    [Fact]
    public async Task Desligado_ou_sem_nome_nao_chama_o_docker()
    {
        var docker = new DockerFalso(noAr: false);
        await using (var gerenciador = Criar(docker, ajustar: o => o.Gerenciar = false))
        {
            await using (await gerenciador.UsarAsync(Nome, Pronto(docker), null, CancellationToken.None))
            {
            }
        }

        await using (var gerenciador = Criar(docker))
        {
            await using (await gerenciador.UsarAsync("", Pronto(docker), null, CancellationToken.None))
            {
            }
        }

        Assert.Empty(docker.Comandos);
    }

    [Fact]
    public async Task Servico_que_nao_responde_depois_de_ligar_da_erro_claro()
    {
        var docker = new DockerFalso(noAr: false);
        await using var gerenciador = Criar(docker, ajustar: o => o.TempoMaximoSubida = TimeSpan.FromMilliseconds(50));

        var erro = await Assert.ThrowsAsync<ServicoIndisponivelException>(() =>
            gerenciador.UsarAsync(Nome, _ => Task.FromResult(false), null, CancellationToken.None));
        Assert.Contains("foi ligado, mas o serviço não respondeu", erro.Message, StringComparison.Ordinal);
    }

    private static HttpClient Cliente(GerenciadorConteineres gerenciador, DockerFalso docker, List<string> recebidas) =>
        new(new ConteinerSobDemanda(gerenciador, Nome, "health")
        {
            InnerHandler = new FakeHttpHandler((r, _) =>
            {
                if (!docker.NoAr)
                {
                    throw new HttpRequestException("conexão recusada");
                }

                recebidas.Add(r.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        })
        { BaseAddress = new Uri("http://localhost:8000/") };

    [Fact]
    public async Task Chamada_liga_o_conteiner_e_tirar_da_gpu_nao_liga()
    {
        var docker = new DockerFalso(noAr: false);
        var recebidas = new List<string>();
        await using var gerenciador = Criar(docker);
        using var http = Cliente(gerenciador, docker, recebidas);

        using var descarregar = ConteinerSobDemanda.MensagemSemLigar(HttpMethod.Post, new Uri("descarregar", UriKind.Relative));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(descarregar));
        Assert.False(docker.NoAr);
        Assert.Empty(recebidas);

        using var resposta = await http.GetAsync(new Uri("v1/models", UriKind.Relative));
        Assert.True(docker.NoAr);
        Assert.Equal(["/health", "/v1/models"], recebidas);
    }

    [Fact]
    public async Task Conteiner_parado_fora_da_lib_e_ligado_de_novo_na_chamada()
    {
        var docker = new DockerFalso(noAr: true);
        var recebidas = new List<string>();
        await using var gerenciador = Criar(docker);
        using var http = Cliente(gerenciador, docker, recebidas);

        (await http.GetAsync(new Uri("v1/models", UriKind.Relative))).Dispose();
        docker.Parar(); // docker stop à mão
        (await http.GetAsync(new Uri("v1/models", UriKind.Relative))).Dispose();

        Assert.True(docker.NoAr);
        Assert.Equal(1, docker.Quantos("start"));
        Assert.Equal(2, recebidas.Count(r => r == "/v1/models"));
    }
}
