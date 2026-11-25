using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Processos;
using Transcritor.Core.Erros;

namespace Transcritor.Arquivo;

/// <summary>
/// Dá a vez na GPU a um processamento de arquivo por vez na máquina e tira o tempo real do caminho enquanto ele roda.
/// <list type="bullet">
/// <item>Vez: trava num arquivo (<see cref="OpcoesArquivo.ArquivoTravaGpu"/>). Outro processo que use a lib espera a vez
/// em vez de carregar um segundo modelo junto (o Docker Desktop travou assim, com o Whisper de uma execução e o modelo de
/// 8 GB do Ollama de outra na placa de 12 GB). O sistema solta a trava se o processo morrer. Programas fora da lib
/// entram na fila travando o mesmo arquivo (byte <see cref="PosicaoTrava"/>, 1 byte, por LockFileEx/msvcrt.locking).</item>
/// <item>Tempo real: o WhisperLiveKit carrega o modelo ao subir e só solta parando o contêiner. Se ele estiver no ar, é
/// parado durante o arquivo e religado no fim (<see cref="OpcoesArquivo.PararTempoRealDuranteArquivo"/>).</item>
/// </list>
/// </summary>
public sealed class CoordenadorGpu(IExecutorProcesso executor, IOptions<OpcoesArquivo> opcoes)
{
    /// <summary>Byte travado no arquivo (fora do texto que diz quem está com a vez, que os outros podem ler).</summary>
    public const long PosicaoTrava = 1 << 20;

    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    internal TimeSpan IntervaloEspera { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Pega a vez (esperando, se outro processo estiver com ela) e para o tempo real. Devolve o que solta tudo e religa
    /// o tempo real no fim.
    /// </summary>
    /// <param name="aoEsperar">Chamado uma vez, com quem está com a vez, se for preciso esperar.</param>
    /// <param name="cancellationToken">Cancelamento da espera.</param>
    public async Task<IAsyncDisposable> ReservarAsync(Action<string>? aoEsperar, CancellationToken cancellationToken)
    {
        var trava = _opcoes.CoordenarGpu ? await TravarAsync(aoEsperar, cancellationToken).ConfigureAwait(false) : null;
        try
        {
            var parado = _opcoes.PararTempoRealDuranteArquivo ? await PararTempoRealAsync(cancellationToken).ConfigureAwait(false) : null;
            return new Reserva(this, trava, parado);
        }
        catch
        {
            if (trava is not null)
            {
                await trava.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<FileStream> TravarAsync(Action<string>? aoEsperar, CancellationToken cancellationToken)
    {
        var caminho = _opcoes.ArquivoTravaGpu;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
        var avisou = false;
        while (true)
        {
            FileStream arquivo;
            try
            {
                // No macOS não há trava de trecho: o arquivo inteiro fica exclusivo.
                arquivo = new FileStream(
                    caminho, FileMode.OpenOrCreate, FileAccess.ReadWrite, OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                arquivo = null!;
            }

            try
            {
                if (arquivo is null)
                {
                    throw new IOException("Arquivo da vez aberto por outro processo.");
                }

                if (!OperatingSystem.IsMacOS())
                {
                    arquivo.Lock(PosicaoTrava, 1);
                }

                arquivo.SetLength(0);
                var dono = string.Create(
                    CultureInfo.InvariantCulture,
                    $"transcritor-lib, processo {Environment.ProcessId} ({Process.GetCurrentProcess().ProcessName}), desde {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await arquivo.WriteAsync(System.Text.Encoding.UTF8.GetBytes(dono), cancellationToken).ConfigureAwait(false);
                await arquivo.FlushAsync(cancellationToken).ConfigureAwait(false);
                return arquivo;
            }
            catch (IOException)
            {
                if (arquivo is not null)
                {
                    await arquivo.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!avisou)
            {
                avisou = true;
                aoEsperar?.Invoke(LerDono(caminho));
            }

            await Task.Delay(IntervaloEspera, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string LerDono(string caminho)
    {
        try
        {
            using var leitura = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var texto = new StreamReader(leitura);
            var dono = texto.ReadLine();
            return string.IsNullOrWhiteSpace(dono) ? "outro processo" : dono.Trim();
        }
        catch (IOException)
        {
            return "outro processo";
        }
    }

    /// <summary>Para o contêiner do tempo real se ele estiver no ar; devolve o nome para religar, ou nulo.</summary>
    private async Task<string?> PararTempoRealAsync(CancellationToken cancellationToken)
    {
        var conteiner = _opcoes.ConteinerTempoReal;
        if (string.IsNullOrWhiteSpace(conteiner))
        {
            return null;
        }

        var estado = await DockerAsync(["inspect", "-f", "{{.State.Running}}", conteiner], cancellationToken).ConfigureAwait(false);
        if (estado is not { CodigoSaida: 0 } || estado.Saida.Trim() != "true")
        {
            return null;
        }

        var parar = await DockerAsync(["stop", conteiner], cancellationToken).ConfigureAwait(false);
        return parar is { CodigoSaida: 0 } ? conteiner : null;
    }

    private async Task ReligarAsync(string conteiner)
    {
        // Sem o token do chamador: o tempo real volta mesmo se o processamento foi cancelado.
        await DockerAsync(["start", conteiner], CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Roda o docker; nulo se ele não existe nesta máquina (a lib segue sem mexer no tempo real).</summary>
    private async Task<ResultadoProcesso?> DockerAsync(IReadOnlyList<string> argumentos, CancellationToken cancellationToken)
    {
        try
        {
            return await executor.ExecutarAsync(_opcoes.CaminhoDocker, argumentos, null, cancellationToken).ConfigureAwait(false);
        }
        catch (FerramentaExternaException)
        {
            return null;
        }
    }

    private sealed class Reserva(CoordenadorGpu dono, FileStream? trava, string? conteinerParado) : IAsyncDisposable
    {
        private int _solta;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _solta, 1) == 1)
            {
                return;
            }

            try
            {
                if (conteinerParado is not null)
                {
                    await dono.ReligarAsync(conteinerParado).ConfigureAwait(false);
                }
            }
            finally
            {
                if (trava is not null)
                {
                    await trava.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
