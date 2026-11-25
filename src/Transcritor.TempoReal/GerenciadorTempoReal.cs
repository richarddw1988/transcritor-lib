using Microsoft.Extensions.Options;
using Transcritor.Core.Erros;
using Transcritor.Core.Gpu;
using Transcritor.Core.Processos;
using Transcritor.TempoReal.WhisperLiveKit;

namespace Transcritor.TempoReal;

/// <summary>
/// Liga e desliga o tempo real conforme o uso, uma instância por programa (singleton):
/// <list type="bullet">
/// <item>A primeira sessão aberta pega a vez na GPU com prioridade (<see cref="VezGpu"/>) e, se o contêiner do
/// WhisperLiveKit estiver parado, liga ele e espera aceitar conexão. As sessões seguintes, enquanto houver alguma aberta,
/// entram direto.</item>
/// <item>Quando a última sessão fecha, a vez é solta na hora (o processamento de arquivo pode seguir; ele mesmo para o
/// contêiner ocioso) e o contêiner é desligado depois de <see cref="OpcoesTempoReal.DesligarAposOcioso"/>, se nenhuma
/// sessão abrir antes.</item>
/// </list>
/// </summary>
public sealed class GerenciadorTempoReal : IAsyncDisposable, IDisposable
{
    private readonly OpcoesTempoReal _opcoes;
    private readonly ConteinerDocker _conteiner;
    private readonly SemaphoreSlim _trava = new(1, 1);
    private int _sessoes;
    private IAsyncDisposable? _vez;
    private CancellationTokenSource? _ocioso;
    private Task _desligar = Task.CompletedTask;
    private bool _descartado;

    public GerenciadorTempoReal(IExecutorProcesso executor, MotorWhisperLiveKit motor, IOptions<OpcoesTempoReal> opcoes)
    {
        ArgumentNullException.ThrowIfNull(motor);
        ArgumentNullException.ThrowIfNull(opcoes);
        _opcoes = opcoes.Value;
        _conteiner = new ConteinerDocker(executor, _opcoes.CaminhoDocker, _opcoes.ConteinerTempoReal);
        VerificarServico = motor.VerificarAsync;
    }

    /// <summary>Confere se o WhisperLiveKit aceita conexão (troca nos testes).</summary>
    internal Func<CancellationToken, Task> VerificarServico { get; set; }

    internal TimeSpan IntervaloEspera { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Contêiner do WhisperLiveKit: true no ar, false parado (liga sozinho na próxima sessão), nulo sem docker, sem o
    /// contêiner ou com <see cref="OpcoesTempoReal.GerenciarConteiner"/> desligado.
    /// </summary>
    public Task<bool?> ConteinerNoArAsync(CancellationToken cancellationToken) =>
        _opcoes.GerenciarConteiner ? _conteiner.NoArAsync(cancellationToken) : Task.FromResult<bool?>(null);

    /// <summary>Abre uma sessão: pega a vez e liga o WhisperLiveKit, se preciso. Descartar o retorno fecha a sessão.</summary>
    /// <param name="aoEsperar">Chamado com o motivo enquanto espera (vez na GPU, contêiner ligando).</param>
    /// <param name="cancellationToken">Cancela a espera.</param>
    public async Task<IAsyncDisposable> AbrirSessaoAsync(Action<string>? aoEsperar, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_descartado, this);
        await _trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ocioso is not null)
            {
                await _ocioso.CancelAsync().ConfigureAwait(false);
                _ocioso.Dispose();
                _ocioso = null;
            }

            if (_sessoes == 0)
            {
                if (_opcoes.CoordenarGpu)
                {
                    _vez = await VezGpu.PegarAsync(
                        _opcoes.ArquivoTravaGpu,
                        VezGpu.Dono("tempo real"),
                        prioridade: true,
                        motivo => aoEsperar?.Invoke($"Esperando a vez na GPU: {motivo}."),
                        IntervaloEspera,
                        cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await LigarSePrecisoAsync(aoEsperar, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await SoltarVezAsync().ConfigureAwait(false);
                    throw;
                }
            }

            _sessoes++;
            return new Sessao(this);
        }
        finally
        {
            _trava.Release();
        }
    }

    private async Task LigarSePrecisoAsync(Action<string>? aoEsperar, CancellationToken cancellationToken)
    {
        if (!_opcoes.GerenciarConteiner || await _conteiner.NoArAsync(cancellationToken).ConfigureAwait(false) != false)
        {
            return; // no ar, ou sem docker/contêiner: a conexão diz se o serviço responde
        }

        aoEsperar?.Invoke($"Ligando o reconhecimento em tempo real (contêiner {_conteiner.Nome}).");
        var ligar = await _conteiner.LigarAsync(cancellationToken).ConfigureAwait(false);
        if (ligar is not { CodigoSaida: 0 })
        {
            throw new ServicoIndisponivelException(
                $"Não foi possível ligar o contêiner {_conteiner.Nome}: {ligar?.Erro.Trim()}");
        }

        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(_opcoes.TempoMaximoSubida);
        Exception? ultimo = null;
        while (true)
        {
            try
            {
                await VerificarServico(prazo.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is ServicoIndisponivelException or System.Net.WebSockets.WebSocketException or IOException
                or HttpRequestException)
            {
                ultimo = ex;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            try
            {
                await Task.Delay(IntervaloEspera, prazo.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ServicoIndisponivelException(
                    $"O contêiner {_conteiner.Nome} foi ligado mas o WhisperLiveKit não aceitou conexão em {_opcoes.TempoMaximoSubida.TotalSeconds:F0} s.",
                    ultimo);
            }
        }
    }

    private async Task FecharSessaoAsync()
    {
        if (_descartado)
        {
            return; // o descarte já soltou a vez
        }

        await _trava.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--_sessoes > 0)
            {
                return;
            }

            await SoltarVezAsync().ConfigureAwait(false);
            if (_opcoes.GerenciarConteiner && _opcoes.DesligarAposOcioso is { } espera && !_descartado)
            {
                _ocioso = new CancellationTokenSource();
                _desligar = DesligarDepoisAsync(espera, _ocioso.Token);
            }
        }
        finally
        {
            _trava.Release();
        }
    }

    private async Task DesligarDepoisAsync(TimeSpan espera, CancellationToken cancelar)
    {
        try
        {
            await Task.Delay(espera, cancelar).ConfigureAwait(false);
            await _trava.WaitAsync(cancelar).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // abriu outra sessão
        }

        try
        {
            if (_sessoes == 0 && !cancelar.IsCancellationRequested)
            {
                await PararSeNoArAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _trava.Release();
        }
    }

    private async Task PararSeNoArAsync()
    {
        if (await _conteiner.NoArAsync(CancellationToken.None).ConfigureAwait(false) == true)
        {
            await _conteiner.PararAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SoltarVezAsync()
    {
        var vez = _vez;
        _vez = null;
        if (vez is not null)
        {
            await vez.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Faz na hora um desligamento que estava esperando o tempo ocioso.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_descartado)
        {
            return;
        }

        await _trava.WaitAsync().ConfigureAwait(false);
        var pendente = false;
        try
        {
            _descartado = true;
            if (_ocioso is not null)
            {
                pendente = true;
                await _ocioso.CancelAsync().ConfigureAwait(false);
                _ocioso.Dispose();
                _ocioso = null;
            }

            if (pendente && _sessoes == 0)
            {
                await PararSeNoArAsync().ConfigureAwait(false);
            }

            await SoltarVezAsync().ConfigureAwait(false);
        }
        finally
        {
            _trava.Release();
        }

        await _desligar.ConfigureAwait(false);
        _trava.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Sessao(GerenciadorTempoReal dono) : IAsyncDisposable
    {
        private int _fechada;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _fechada, 1) == 0)
            {
                await dono.FecharSessaoAsync().ConfigureAwait(false);
            }
        }
    }
}
