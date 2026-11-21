using Transcritor.Core.Modelos;

namespace Transcritor.Core.Contratos;

/// <summary>Processa um arquivo de áudio ou vídeo e devolve frases e palavras com tempo.</summary>
public interface ITranscritorArquivo
{
    /// <param name="caminhoArquivo">Áudio ou vídeo em qualquer formato que o ffmpeg leia.</param>
    /// <param name="saida">
    /// Idiomas pedidos. O texto fica sempre no idioma falado; <see cref="Frase.Traducao"/> é preenchida
    /// quando a saída pede o outro idioma (ou <see cref="Saida.Ambos"/>).
    /// </param>
    /// <param name="progresso">Relatório de progresso opcional.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task<ResultadoTranscricao> ProcessarAsync(
        string caminhoArquivo,
        Saida saida,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default);
}
