using Transcritor.Core.Modelos;

namespace Transcritor.Core.Contratos;

/// <summary>Motor que transcreve um WAV 16 kHz mono com tempo por palavra (ex.: Speaches).</summary>
public interface IMotorTranscricao
{
    /// <param name="caminhoWav">WAV PCM 16 kHz mono.</param>
    /// <param name="idioma">Idioma falado, se conhecido; nulo para detecção automática.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, CancellationToken cancellationToken);

    /// <summary>Identifica o motor e o modelo; entra na chave do cache.</summary>
    string Identificador { get; }
}
