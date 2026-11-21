using System.Buffers;
using System.Security.Cryptography;

namespace Transcritor.Arquivo;

internal static class HashArquivo
{
    /// <summary>SHA-256 do conteúdo, em hexadecimal minúsculo.</summary>
    /// <param name="caminho">Arquivo.</param>
    /// <param name="aoAvancar">Recebe o percentual lido (0 a 100).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    public static async Task<string> CalcularAsync(string caminho, Action<double>? aoAvancar, CancellationToken cancellationToken)
    {
        const int tamanhoBuffer = 1024 * 1024;
        await using var arquivo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, tamanhoBuffer, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(tamanhoBuffer);
        try
        {
            var total = arquivo.Length;
            long lido = 0;
            int n;
            while ((n = await arquivo.ReadAsync(buffer.AsMemory(0, tamanhoBuffer), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                lido += n;
                if (total > 0)
                {
                    aoAvancar?.Invoke(lido * 100d / total);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
