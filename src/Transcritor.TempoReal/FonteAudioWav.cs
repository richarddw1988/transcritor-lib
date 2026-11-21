using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.TempoReal;

/// <summary>
/// Fonte de áudio a partir de um WAV PCM s16le 16 kHz mono, entregue no ritmo do tempo real
/// (ou mais rápido, com <paramref name="velocidade"/>). Serve para testar o tempo real sem microfone.
/// </summary>
/// <param name="caminhoWav">WAV 16 kHz mono 16 bits (gere com ffmpeg -ac 1 -ar 16000 -c:a pcm_s16le).</param>
/// <param name="velocidade">1 = tempo real; 2 = duas vezes mais rápido; 0 = sem espera.</param>
public sealed class FonteAudioWav(string caminhoWav, double velocidade = 1) : IFonteAudio
{
    private const int TaxaAmostragem = 16000;
    private const int BytesPorBloco = 3200; // 100 ms

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CapturarAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dados = await LerPcmAsync(caminhoWav, cancellationToken).ConfigureAwait(false);
        var relogio = System.Diagnostics.Stopwatch.StartNew();

        for (var pos = 0; pos < dados.Length; pos += BytesPorBloco)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var bloco = dados.AsMemory(pos, Math.Min(BytesPorBloco, dados.Length - pos));
            yield return bloco;

            if (velocidade > 0)
            {
                var alvo = TimeSpan.FromSeconds((pos + bloco.Length) / 2d / TaxaAmostragem / velocidade);
                var espera = alvo - relogio.Elapsed;
                if (espera > TimeSpan.Zero && !await Esperar(espera, cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Lê o bloco "data" de um WAV PCM 16 kHz mono 16 bits.</summary>
    internal static async Task<byte[]> LerPcmAsync(string caminho, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(caminho, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new TranscritorException($"'{caminho}' não é um WAV.");
        }

        var pos = 12;
        var formatoOk = false;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(pos, 4);
            var tamanho = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            var corpo = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                var formato = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(corpo, 2));
                var canais = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(corpo + 2, 2));
                var taxa = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(corpo + 4, 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(corpo + 14, 2));
                formatoOk = formato is 1 or 0xFFFE && canais == 1 && taxa == TaxaAmostragem && bits == 16;
                if (!formatoOk)
                {
                    throw new TranscritorException(
                        $"'{caminho}' precisa ser PCM 16 kHz mono 16 bits (é {taxa} Hz, {canais} canal(is), {bits} bits).");
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!formatoOk)
                {
                    throw new TranscritorException($"'{caminho}' não tem o bloco fmt antes dos dados.");
                }

                var fim = Math.Min(bytes.Length, corpo + tamanho);
                return bytes[corpo..fim];
            }

            pos = corpo + tamanho + (tamanho % 2);
        }

        throw new TranscritorException($"'{caminho}' não tem bloco de dados.");
    }

    private static async Task<bool> Esperar(TimeSpan tempo, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(tempo, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
