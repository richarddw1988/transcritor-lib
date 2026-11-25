using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Transcritor.Core.Erros;

namespace Transcritor.Core.Processos;

/// <summary>Implementação com <see cref="Process"/>.</summary>
public sealed class ExecutorProcesso : IExecutorProcesso
{
    public async Task<ResultadoProcesso> ExecutarAsync(
        string executavel,
        IReadOnlyList<string> argumentos,
        Action<string>? aoLerLinha,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argumentos);

        var inicio = new ProcessStartInfo(executavel)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argumento in argumentos)
        {
            inicio.ArgumentList.Add(argumento);
        }

        using var processo = new Process { StartInfo = inicio };
        var saida = new StringBuilder();
        var erro = new StringBuilder();
        processo.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            saida.AppendLine(e.Data);
            aoLerLinha?.Invoke(e.Data);
        };
        processo.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                erro.AppendLine(e.Data);
            }
        };

        try
        {
            processo.Start();
        }
        catch (Win32Exception ex)
        {
            throw new FerramentaExternaException(
                $"Não foi possível executar '{executavel}'. Instale o ffmpeg (winget install Gyan.FFmpeg) ou informe o caminho completo nas opções.",
                ex);
        }

        processo.BeginOutputReadLine();
        processo.BeginErrorReadLine();

        try
        {
            await processo.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                processo.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Já terminou.
            }

            throw;
        }

        // Garante que os eventos de leitura terminaram.
        processo.WaitForExit();
        return new ResultadoProcesso(processo.ExitCode, saida.ToString(), erro.ToString());
    }
}
