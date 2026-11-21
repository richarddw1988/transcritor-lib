using System.Buffers.Binary;

namespace Transcritor.Arquivo.Midia;

/// <summary>Recorta um trecho de um WAV PCM sem chamar o ffmpeg (lê os chunks RIFF e copia os bytes).</summary>
internal static class RecorteWav
{
    /// <exception cref="InvalidDataException">O arquivo não é um WAV PCM legível.</exception>
    public static async Task RecortarAsync(string origem, string destino, double inicio, double fim, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(origem, cancellationToken).ConfigureAwait(false);
        var (formato, dados, tamanhoDados) = LerChunks(bytes);

        var canais = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(2));
        var taxa = BinaryPrimitives.ReadInt32LittleEndian(formato.AsSpan(4));
        var alinhamento = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(14));
        if (taxa <= 0 || alinhamento == 0)
        {
            throw new InvalidDataException("Cabeçalho 'fmt ' do WAV inválido.");
        }

        var quadros = tamanhoDados / alinhamento;
        var de = Math.Clamp((long)Math.Floor(inicio * taxa), 0, quadros);
        var ate = Math.Clamp((long)Math.Ceiling(fim * taxa), de, quadros);
        var tamanho = (int)((ate - de) * alinhamento);

        var saida = new byte[44 + tamanho];
        "RIFF"u8.CopyTo(saida);
        BinaryPrimitives.WriteInt32LittleEndian(saida.AsSpan(4), 36 + tamanho);
        "WAVE"u8.CopyTo(saida.AsSpan(8));
        "fmt "u8.CopyTo(saida.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(saida.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(saida.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(saida.AsSpan(22), canais);
        BinaryPrimitives.WriteInt32LittleEndian(saida.AsSpan(24), taxa);
        BinaryPrimitives.WriteInt32LittleEndian(saida.AsSpan(28), taxa * alinhamento);
        BinaryPrimitives.WriteUInt16LittleEndian(saida.AsSpan(32), alinhamento);
        BinaryPrimitives.WriteUInt16LittleEndian(saida.AsSpan(34), bits);
        "data"u8.CopyTo(saida.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(saida.AsSpan(40), tamanho);
        Array.Copy(bytes, dados + (de * alinhamento), saida, 44, tamanho);

        await File.WriteAllBytesAsync(destino, saida, cancellationToken).ConfigureAwait(false);
    }

    private static (byte[] Formato, int InicioDados, long TamanhoDados) LerChunks(byte[] bytes)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("O arquivo não é um WAV (RIFF/WAVE).");
        }

        byte[]? formato = null;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(pos, 4);
            var tamanho = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
            var corpo = pos + 8;
            if (id.SequenceEqual("fmt "u8) && tamanho >= 16 && corpo + 16 <= bytes.Length)
            {
                formato = bytes.AsSpan(corpo, 16).ToArray();
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (formato is null)
                {
                    break;
                }

                // ffmpeg em pipe grava tamanho 0xFFFFFFFF: usa o que existe no arquivo.
                var disponivel = bytes.Length - corpo;
                return (formato, corpo, Math.Min(tamanho, disponivel));
            }

            pos = corpo + (int)Math.Min(tamanho + (tamanho & 1), int.MaxValue - corpo);
        }

        throw new InvalidDataException("WAV sem os chunks 'fmt ' e 'data'.");
    }
}
