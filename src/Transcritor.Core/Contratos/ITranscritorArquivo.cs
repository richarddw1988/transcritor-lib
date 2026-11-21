using Transcritor.Core.Modelos;

namespace Transcritor.Core.Contratos;

/// <summary>Processa um arquivo de áudio ou vídeo e devolve frases e palavras com tempo.</summary>
public interface ITranscritorArquivo
{
    /// <param name="caminhoArquivo">Áudio ou vídeo em qualquer formato que o ffmpeg leia.</param>
    /// <param name="progresso">Relatório de progresso opcional.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task<ResultadoTranscricao> ProcessarAsync(
        string caminhoArquivo,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default);
}
