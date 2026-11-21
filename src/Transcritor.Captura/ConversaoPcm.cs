using System.Buffers.Binary;
using NAudio.Wave;

namespace Transcritor.Captura;

/// <summary>Conversões puras usadas na captura (testáveis sem dispositivo de áudio).</summary>
internal static class ConversaoPcm
{
    /// <summary>Média dos canais de cada quadro. <paramref name="destino"/> precisa de origem.Length / canais posições.</summary>
    public static void MisturarParaMono(ReadOnlySpan<float> intercalado, int canais, Span<float> destino)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(canais, 1);
        var quadros = intercalado.Length / canais;
        for (var q = 0; q < quadros; q++)
        {
            var soma = 0f;
            var baseQuadro = q * canais;
            for (var c = 0; c < canais; c++)
            {
                soma += intercalado[baseQuadro + c];
            }

            destino[q] = soma / canais;
        }
    }

    /// <summary>float [-1, 1] → PCM s16le (com saturação). <paramref name="destino"/> precisa de 2 bytes por amostra.</summary>
    public static void FloatParaPcm16(ReadOnlySpan<float> amostras, Span<byte> destino)
    {
        for (var i = 0; i < amostras.Length; i++)
        {
            var v = Math.Clamp(amostras[i], -1f, 1f);
            var s = (short)Math.Round(v * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destino.Slice(i * 2, 2), s);
        }
    }
}

/// <summary>ISampleProvider que reduz qualquer número de canais para mono pela média.</summary>
internal sealed class MonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _origem;
    private readonly int _canais;
    private float[] _buffer = [];

    public MonoSampleProvider(ISampleProvider origem)
    {
        _origem = origem;
        _canais = origem.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(origem.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_canais == 1)
        {
            return _origem.Read(buffer, offset, count);
        }

        var necessario = count * _canais;
        if (_buffer.Length < necessario)
        {
            _buffer = new float[necessario];
        }

        var lidas = _origem.Read(_buffer, 0, necessario);
        var quadros = lidas / _canais;
        ConversaoPcm.MisturarParaMono(_buffer.AsSpan(0, quadros * _canais), _canais, buffer.AsSpan(offset, quadros));
        return quadros;
    }
}
