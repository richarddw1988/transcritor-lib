using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Transcritor.Core.Gpu;

/// <summary>
/// A vez na GPU entre processos da máquina, por um arquivo de trava (o sistema solta a trava se o processo morrer).
/// <list type="bullet">
/// <item>Byte <see cref="PosicaoVez"/>: quem o trava está usando a GPU. A linha 1 do arquivo diz quem é.</item>
/// <item>Byte <see cref="PosicaoPedido"/>: o tempo real trava enquanto quer a vez ou está com ela. Quem não tem prioridade
/// (o processamento de arquivo) não pega a vez enquanto ele estiver travado, e assim cede entre uma etapa e outra.</item>
/// </list>
/// No Windows a trava é por handle (vale também entre partes do mesmo processo). No macOS não há trava de trecho: o
/// arquivo inteiro fica exclusivo e não há prioridade.
/// </summary>
public static class VezGpu
{
    /// <summary>Byte da vez (1 MiB, fora do texto que diz quem está com a vez).</summary>
    public const long PosicaoVez = 1 << 20;

    /// <summary>Byte do pedido de prioridade do tempo real.</summary>
    public const long PosicaoPedido = PosicaoVez + 1;

    /// <summary>%LOCALAPPDATA%\Transcritor\gpu.lock (ou o equivalente do sistema).</summary>
    public static string CaminhoPadrao { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "gpu.lock");

    /// <summary>Texto que vai na linha 1 do arquivo: "transcritor-lib (papel), processo N (nome), desde ...".</summary>
    public static string Dono(string papel)
    {
        using var processo = Process.GetCurrentProcess();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"transcritor-lib ({papel}), processo {Environment.ProcessId} ({processo.ProcessName}), desde {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>Pega a vez, esperando se outro estiver com ela (ou, sem prioridade, se o tempo real a pediu).</summary>
    /// <param name="caminho">Arquivo de trava.</param>
    /// <param name="dono">Quem pega (ver <see cref="Dono"/>).</param>
    /// <param name="prioridade">Tempo real: pede a vez e o processamento de arquivo cede na próxima etapa.</param>
    /// <param name="aoEsperar">Chamado com o motivo cada vez que o motivo da espera muda.</param>
    /// <param name="intervalo">De quanto em quanto tempo tenta de novo.</param>
    /// <param name="cancellationToken">Cancela a espera.</param>
    /// <returns>O que solta a vez (e o pedido).</returns>
    public static async Task<IAsyncDisposable> PegarAsync(
        string caminho,
        string dono,
        bool prioridade,
        Action<string>? aoEsperar,
        TimeSpan intervalo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caminho);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(caminho))!);
        string? ultimoMotivo = null;
        void Avisar(string motivo)
        {
            if (motivo != ultimoMotivo)
            {
                ultimoMotivo = motivo;
                aoEsperar?.Invoke(motivo);
            }
        }

        FileStream? pedido = null; // com prioridade: o handle que segura o pedido enquanto espera a vez
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var arquivo = pedido ?? Abrir(caminho);
                if (arquivo is not null)
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        return await EntregarAsync(arquivo, dono, cancellationToken).ConfigureAwait(false);
                    }

                    if (prioridade)
                    {
                        if (pedido is not null || Travar(arquivo, PosicaoPedido))
                        {
                            pedido = arquivo;
                            if (Travar(arquivo, PosicaoVez))
                            {
                                pedido = null;
                                return await EntregarAsync(arquivo, dono, cancellationToken).ConfigureAwait(false);
                            }

                            Avisar(LerDono(caminho));
                        }
                        else
                        {
                            await arquivo.DisposeAsync().ConfigureAwait(false);
                            Avisar("outra sessão de tempo real");
                        }
                    }
                    else if (!Livre(arquivo, PosicaoPedido))
                    {
                        await arquivo.DisposeAsync().ConfigureAwait(false);
                        Avisar("uma sessão de tempo real, que tem prioridade");
                    }
                    else if (Travar(arquivo, PosicaoVez))
                    {
                        return await EntregarAsync(arquivo, dono, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await arquivo.DisposeAsync().ConfigureAwait(false);
                        Avisar(LerDono(caminho));
                    }
                }
                else
                {
                    Avisar(LerDono(caminho));
                }

                await Task.Delay(intervalo, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (pedido is not null)
            {
                await pedido.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static FileStream? Abrir(string caminho)
    {
        try
        {
            return new FileStream(
                caminho,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Travar(FileStream arquivo, long posicao)
    {
        if (OperatingSystem.IsMacOS())
        {
            return true;
        }

        try
        {
            arquivo.Lock(posicao, 1);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Ninguém está segurando o byte (trava e solta na hora).</summary>
    private static bool Livre(FileStream arquivo, long posicao)
    {
        if (OperatingSystem.IsMacOS() || !Travar(arquivo, posicao))
        {
            return OperatingSystem.IsMacOS();
        }

        arquivo.Unlock(posicao, 1);
        return true;
    }

    private static async Task<IAsyncDisposable> EntregarAsync(FileStream arquivo, string dono, CancellationToken cancellationToken)
    {
        try
        {
            arquivo.SetLength(0);
            await arquivo.WriteAsync(Encoding.UTF8.GetBytes(dono), cancellationToken).ConfigureAwait(false);
            await arquivo.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new Vez(arquivo);
        }
        catch
        {
            await arquivo.DisposeAsync().ConfigureAwait(false);
            throw;
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

    private sealed class Vez(FileStream arquivo) : IAsyncDisposable
    {
        private int _solta;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _solta, 1) == 1 ? ValueTask.CompletedTask : arquivo.DisposeAsync();
    }
}
