using System.Diagnostics;
using System.Globalization;
using Transcritor.Core.Erros;

namespace Transcritor.Core.Processos;

/// <summary>
/// Liga os contêineres dos serviços (Speaches, separador de voz, Ollama) quando a lib vai usá-los e os desliga depois de
/// <see cref="OpcoesConteineres.DesligarAposOcioso"/> sem uso. Uma instância por programa (singleton); as chamadas HTTP
/// passam por <see cref="ConteinerSobDemanda"/>, que pega um <see cref="UsarAsync">uso</see> enquanto a chamada dura.
/// <para>
/// Vários programas da máquina podem usar o mesmo contêiner (o Cli processando um filme e o aplicativo traduzindo uma
/// palavra clicada): cada um deixa marcas em <see cref="OpcoesConteineres.PastaUso"/> — "&lt;contêiner&gt;.&lt;pid&gt;.uso"
/// enquanto usa e "&lt;contêiner&gt;.&lt;pid&gt;.ultimo" com a hora do último uso — e só desliga o contêiner se nenhum outro
/// programa vivo estiver usando nem tiver usado dentro do tempo ocioso. Ligar e desligar acontecem com
/// "&lt;contêiner&gt;.trava" travado, para um programa não desligar o que o outro acabou de ligar.
/// </para>
/// </summary>
public sealed class GerenciadorConteineres : IAsyncDisposable, IDisposable
{
    /// <summary>Quanto antes de o tempo ocioso vencer a lib volta a conferir se o contêiner ainda está no ar.</summary>
    private static readonly TimeSpan FolgaConferencia = TimeSpan.FromSeconds(30);

    private readonly IExecutorProcesso _executor;
    private readonly OpcoesConteineres _opcoes;
    private readonly Dictionary<string, Estado> _estados = new(StringComparer.Ordinal);
    private bool _descartado;

    public GerenciadorConteineres(IExecutorProcesso executor, OpcoesConteineres opcoes)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(opcoes);
        _executor = executor;
        _opcoes = opcoes;
    }

    /// <summary>De quanto em quanto tempo confere se o serviço recém-ligado já responde (troca nos testes).</summary>
    internal TimeSpan IntervaloEspera { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Se o processo de um pid está vivo (troca nos testes).</summary>
    internal Func<int, bool> ProcessoVivo { get; set; } = Vivo;

    /// <summary>true no ar, false parado, nulo sem docker, sem o contêiner ou sem gerenciamento.</summary>
    public Task<bool?> NoArAsync(string conteiner, CancellationToken cancellationToken) =>
        Gerencia(conteiner) ? Docker(conteiner).NoArAsync(cancellationToken) : Task.FromResult<bool?>(null);

    /// <summary>
    /// Marca o contêiner como em uso e o liga se estiver parado, esperando <paramref name="pronto"/> responder. Descartar
    /// o retorno solta o uso; sem uso, o contêiner é desligado depois do tempo ocioso.
    /// </summary>
    /// <param name="conteiner">Nome do contêiner; vazio não gerencia nada.</param>
    /// <param name="pronto">Confere se o serviço já responde (ex.: GET /health).</param>
    /// <param name="aoEsperar">Chamado com o motivo quando precisa ligar o contêiner.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    public async Task<IAsyncDisposable> UsarAsync(
        string conteiner,
        Func<CancellationToken, Task<bool>> pronto,
        Action<string>? aoEsperar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pronto);
        if (!Gerencia(conteiner))
        {
            return Nada.Instancia;
        }

        var e = PegarEstado(conteiner);
        await e.Trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_descartado, this);
            if (e.Ocioso is not null)
            {
                await e.Ocioso.CancelAsync().ConfigureAwait(false);
                e.Ocioso.Dispose();
                e.Ocioso = null;
            }

            if (e.Usos == 0)
            {
                await using (await TravarAsync(conteiner, cancellationToken).ConfigureAwait(false))
                {
                    await GarantirNoArAsync(e, conteiner, pronto, aoEsperar, cancellationToken).ConfigureAwait(false);
                    e.Marca = CriarMarcaUso(conteiner);
                }
            }

            e.Usos++;
            return new Uso(this, e, conteiner);
        }
        finally
        {
            e.Trava.Release();
        }
    }

    /// <summary>
    /// A chamada falhou por conexão: confere de novo o contêiner (alguém pode tê-lo parado fora da lib) e o liga se
    /// estiver parado. true se ligou (vale tentar a chamada de novo).
    /// </summary>
    public async Task<bool> ReligarSeParadoAsync(string conteiner, Func<CancellationToken, Task<bool>> pronto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pronto);
        if (!Gerencia(conteiner))
        {
            return false;
        }

        var e = PegarEstado(conteiner);
        await e.Trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (await TravarAsync(conteiner, cancellationToken).ConfigureAwait(false))
            {
                e.Confirmado = false;
                return await GarantirNoArAsync(e, conteiner, pronto, null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            e.Trava.Release();
        }
    }

    private bool Gerencia(string conteiner) => _opcoes.Gerenciar && !string.IsNullOrWhiteSpace(conteiner);

    private ConteinerDocker Docker(string conteiner) => new(_executor, _opcoes.CaminhoDocker, conteiner);

    private Estado PegarEstado(string conteiner)
    {
        lock (_estados)
        {
            if (!_estados.TryGetValue(conteiner, out var e))
            {
                e = new Estado();
                _estados[conteiner] = e;
            }

            return e;
        }
    }

    /// <summary>Liga se estiver parado; true se ligou. Chamado com a trava do estado e a do arquivo.</summary>
    private async Task<bool> GarantirNoArAsync(
        Estado e,
        string conteiner,
        Func<CancellationToken, Task<bool>> pronto,
        Action<string>? aoEsperar,
        CancellationToken cancellationToken)
    {
        // Usado há pouco por este programa: ninguém o desligou (a marca "último uso" segura os outros programas).
        if (e.SemConteiner || (e.Confirmado && UsoRecente(e)))
        {
            return false;
        }

        var docker = Docker(conteiner);
        var noAr = await docker.NoArAsync(cancellationToken).ConfigureAwait(false);
        if (noAr is null)
        {
            e.SemConteiner = true; // sem docker ou serviço fora de contêiner: a lib só chama o serviço
            return false;
        }

        if (noAr == true)
        {
            e.Confirmado = true;
            return false;
        }

        aoEsperar?.Invoke($"Ligando o contêiner {conteiner}.");
        var r = await docker.LigarAsync(cancellationToken).ConfigureAwait(false);
        if (r is not { CodigoSaida: 0 })
        {
            throw new ServicoIndisponivelException(
                $"Não foi possível ligar o contêiner {conteiner} (docker start {conteiner}): {r?.Erro.Trim()}");
        }

        var limite = DateTime.UtcNow + _opcoes.TempoMaximoSubida;
        while (!await ProntoAsync(pronto, cancellationToken).ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= limite)
            {
                throw new ServicoIndisponivelException(
                    $"O contêiner {conteiner} foi ligado, mas o serviço não respondeu em {_opcoes.TempoMaximoSubida}.");
            }

            await Task.Delay(IntervaloEspera, cancellationToken).ConfigureAwait(false);
        }

        e.Confirmado = true;
        return true;
    }

    private bool UsoRecente(Estado e) =>
        _opcoes.DesligarAposOcioso is not { } ocioso || DateTime.UtcNow - e.UltimoUso < ocioso - FolgaConferencia;

    private static async Task<bool> ProntoAsync(Func<CancellationToken, Task<bool>> pronto, CancellationToken cancellationToken)
    {
        try
        {
            return await pronto(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TranscritorException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task SoltarAsync(Estado e, string conteiner)
    {
        await e.Trava.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--e.Usos > 0)
            {
                return;
            }

            if (e.Marca is not null)
            {
                await e.Marca.DisposeAsync().ConfigureAwait(false);
            }

            e.Marca = null;
            e.UltimoUso = DateTime.UtcNow;
            MarcarUltimoUso(conteiner);
            if (!_descartado && !e.SemConteiner && _opcoes.DesligarAposOcioso is { } ocioso)
            {
                e.Ocioso = new CancellationTokenSource();
                e.Desligar = DesligarDepoisAsync(e, conteiner, ocioso, e.Ocioso.Token);
            }
        }
        finally
        {
            e.Trava.Release();
        }
    }

    private async Task DesligarDepoisAsync(Estado e, string conteiner, TimeSpan ocioso, CancellationToken cancelar)
    {
        try
        {
            await Task.Delay(ocioso, cancelar).ConfigureAwait(false);
            await e.Trava.WaitAsync(cancelar).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // voltou a usar
        }

        try
        {
            if (e.Usos == 0 && !cancelar.IsCancellationRequested)
            {
                await PararSeNinguemUsaAsync(e, conteiner).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // marca de uso inacessível: fica no ar, como se outro programa estivesse usando
        }
        finally
        {
            e.Trava.Release();
        }
    }

    /// <summary>Desliga se nenhum outro programa vivo está usando nem usou dentro do tempo ocioso.</summary>
    private async Task PararSeNinguemUsaAsync(Estado e, string conteiner)
    {
        await using (await TravarAsync(conteiner, CancellationToken.None).ConfigureAwait(false))
        {
            if (OutroProgramaUsando(conteiner))
            {
                return;
            }

            var docker = Docker(conteiner);
            if (await docker.NoArAsync(CancellationToken.None).ConfigureAwait(false) == true)
            {
                await docker.PararAsync(CancellationToken.None).ConfigureAwait(false);
            }

            e.Confirmado = false;
            Apagar(CaminhoMarca(conteiner, Environment.ProcessId, "ultimo"));
        }
    }

    private bool OutroProgramaUsando(string conteiner)
    {
        if (!Directory.Exists(_opcoes.PastaUso))
        {
            return false;
        }

        var agora = DateTime.UtcNow;
        foreach (var arquivo in Directory.EnumerateFiles(_opcoes.PastaUso, conteiner + ".*"))
        {
            var partes = Path.GetFileName(arquivo)[(conteiner.Length + 1)..].Split('.');
            if (partes.Length != 2 || !int.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || pid == Environment.ProcessId)
            {
                continue;
            }

            if (!ProcessoVivo(pid))
            {
                Apagar(arquivo); // programa que fechou sem limpar
                continue;
            }

            if (partes[1] == "uso")
            {
                return true;
            }

            if (partes[1] == "ultimo" && (_opcoes.DesligarAposOcioso is not { } ocioso || agora - File.GetLastWriteTimeUtc(arquivo) < ocioso))
            {
                return true;
            }
        }

        return false;
    }

    private string CaminhoMarca(string conteiner, int pid, string tipo) =>
        Path.Combine(_opcoes.PastaUso, string.Create(CultureInfo.InvariantCulture, $"{conteiner}.{pid}.{tipo}"));

    /// <summary>Arquivo aberto enquanto o programa usa o contêiner (o sistema o apaga se o programa morrer, no Windows).</summary>
    private FileStream CriarMarcaUso(string conteiner) =>
        new(CaminhoMarca(conteiner, Environment.ProcessId, "uso"), FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);

    private void MarcarUltimoUso(string conteiner)
    {
        try
        {
            var caminho = CaminhoMarca(conteiner, Environment.ProcessId, "ultimo");
            File.WriteAllBytes(caminho, []);
            File.SetLastWriteTimeUtc(caminho, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Apagar(string caminho)
    {
        try
        {
            File.Delete(caminho);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Trava "&lt;contêiner&gt;.trava" entre programas (arquivo exclusivo; o sistema solta se o programa morrer).</summary>
    private async Task<FileStream> TravarAsync(string conteiner, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_opcoes.PastaUso);
        var caminho = Path.Combine(_opcoes.PastaUso, conteiner + ".trava");
        while (true)
        {
            try
            {
                return new FileStream(caminho, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool Vivo(int pid)
    {
        try
        {
            using var processo = Process.GetProcessById(pid);
            return !processo.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Desliga na hora o que estava esperando o tempo ocioso (se nenhum outro programa estiver usando).</summary>
    public async ValueTask DisposeAsync()
    {
        List<(string Conteiner, Estado Estado)> estados;
        lock (_estados)
        {
            if (_descartado)
            {
                return;
            }

            _descartado = true;
            estados = _estados.Select(p => (p.Key, p.Value)).ToList();
        }

        foreach (var (conteiner, e) in estados)
        {
            await e.Trava.WaitAsync().ConfigureAwait(false);
            try
            {
                if (e.Ocioso is not null)
                {
                    await e.Ocioso.CancelAsync().ConfigureAwait(false);
                    e.Ocioso.Dispose();
                    e.Ocioso = null;
                    if (e.Usos == 0)
                    {
                        await PararSeNinguemUsaAsync(e, conteiner).ConfigureAwait(false);
                    }
                }

                if (e.Marca is not null)
                {
                    await e.Marca.DisposeAsync().ConfigureAwait(false);
                }

                e.Marca = null;
            }
            catch (IOException)
            {
            }
            finally
            {
                e.Trava.Release();
            }

            await e.Desligar.ConfigureAwait(false);
            Apagar(CaminhoMarca(conteiner, Environment.ProcessId, "ultimo"));
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Estado
    {
        public SemaphoreSlim Trava { get; } = new(1, 1);

        public int Usos { get; set; }

        /// <summary>Visto no ar por este programa (não precisa conferir enquanto o uso for recente).</summary>
        public bool Confirmado { get; set; }

        /// <summary>Sem docker ou sem o contêiner: não há o que ligar.</summary>
        public bool SemConteiner { get; set; }

        public DateTime UltimoUso { get; set; }

        public FileStream? Marca { get; set; }

        public CancellationTokenSource? Ocioso { get; set; }

        public Task Desligar { get; set; } = Task.CompletedTask;
    }

    private sealed class Uso(GerenciadorConteineres dono, Estado estado, string conteiner) : IAsyncDisposable
    {
        private int _solto;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _solto, 1) == 0)
            {
                await dono.SoltarAsync(estado, conteiner).ConfigureAwait(false);
            }
        }
    }

    private sealed class Nada : IAsyncDisposable
    {
        public static readonly Nada Instancia = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
