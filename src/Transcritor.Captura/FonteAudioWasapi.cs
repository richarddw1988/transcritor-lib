using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.Captura;

/// <summary>
/// Microfone (WasapiCapture) ou som do sistema (WasapiLoopbackCapture), convertido para PCM s16le 16 kHz mono
/// em blocos de 100 ms. No som do sistema, o Windows não entrega nada enquanto nada toca.
/// </summary>
public sealed class FonteAudioWasapi(Fonte fonte) : IFonteAudio
{
    private const int TaxaSaida = 16000;
    private const int BytesPorBloco = 3200; // 100 ms

    public Fonte Fonte { get; } = fonte;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CapturarAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var canal = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(300)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        using var captura = CriarCaptura();
        var buffer = new BufferedWaveProvider(captura.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        var reamostrado = new WdlResamplingSampleProvider(new MonoSampleProvider(buffer.ToSampleProvider()), TaxaSaida);
        var amostras = new float[TaxaSaida];
        var pcm = new byte[TaxaSaida * 2];
        using var acumulado = new MemoryStream();

        captura.DataAvailable += (_, e) =>
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int lidas;
            while ((lidas = reamostrado.Read(amostras, 0, amostras.Length)) > 0)
            {
                ConversaoPcm.FloatParaPcm16(amostras.AsSpan(0, lidas), pcm);
                acumulado.Write(pcm, 0, lidas * 2);
            }

            if (acumulado.Length < BytesPorBloco)
            {
                return;
            }

            var dados = acumulado.GetBuffer().AsSpan(0, (int)acumulado.Length);
            var completos = dados.Length / BytesPorBloco * BytesPorBloco;
            for (var pos = 0; pos < completos; pos += BytesPorBloco)
            {
                canal.Writer.TryWrite(dados.Slice(pos, BytesPorBloco).ToArray());
            }

            var resto = dados[completos..].ToArray();
            acumulado.SetLength(0);
            acumulado.Write(resto);
        };
        captura.RecordingStopped += (_, e) => canal.Writer.TryComplete(
            e.Exception is null ? null : new TranscritorException($"A captura de áudio ({Fonte}) parou com erro: {e.Exception.Message}", e.Exception));

        try
        {
            captura.StartRecording();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            throw new TranscritorException($"Não foi possível iniciar a captura ({Fonte}): {ex.Message}", ex);
        }

        try
        {
            while (await EsperarDadosAsync(canal.Reader, cancellationToken).ConfigureAwait(false))
            {
                while (canal.Reader.TryRead(out var bloco))
                {
                    yield return bloco;
                }
            }
        }
        finally
        {
            captura.StopRecording();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Nome do dispositivo padrão usado por esta fonte.</summary>
    public string NomeDispositivo()
    {
        using var enumerador = new MMDeviceEnumerator();
        using var dispositivo = enumerador.GetDefaultAudioEndpoint(
            Fonte == Fonte.Microfone ? DataFlow.Capture : DataFlow.Render, Role.Console);
        return dispositivo.FriendlyName;
    }

    private WasapiCapture CriarCaptura()
    {
        try
        {
            return Fonte == Fonte.Microfone ? new WasapiCapture() : new WasapiLoopbackCapture();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new TranscritorException(
                Fonte == Fonte.Microfone
                    ? "Nenhum microfone disponível (verifique o dispositivo padrão de gravação do Windows)."
                    : "Nenhum dispositivo de saída de áudio disponível para capturar o som do sistema.",
                ex);
        }
    }

    /// <summary>Falso quando cancelado (fim normal) ou quando a captura terminou.</summary>
    private static async Task<bool> EsperarDadosAsync(ChannelReader<byte[]> leitor, CancellationToken cancellationToken)
    {
        try
        {
            return await leitor.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>Cria fontes WASAPI.</summary>
public sealed class FabricaFonteAudioWindows : IFabricaFonteAudio
{
    public IFonteAudio Criar(Fonte fonte) => new FonteAudioWasapi(fonte);
}
