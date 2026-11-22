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

    /// <summary>
    /// Processa vários arquivos, uma etapa por vez para todos (transcreve todos, depois corrige, depois traduz...), para
    /// cada modelo carregar uma vez por lote em vez de uma vez por arquivo. Devolve um resultado por arquivo, na mesma
    /// ordem. Cada etapa grava no cache assim que termina: se o lote parar no meio, a próxima vez continua de onde parou.
    /// A implementação padrão processa um arquivo por vez.
    /// </summary>
    /// <param name="caminhosArquivos">Áudios ou vídeos.</param>
    /// <param name="saida">Idiomas pedidos (os mesmos para todos).</param>
    /// <param name="progresso">Relatório de progresso; <see cref="ProgressoProcessamento.Arquivo"/> diz de qual arquivo é.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    async Task<IReadOnlyList<ResultadoTranscricao>> ProcessarLoteAsync(
        IReadOnlyList<string> caminhosArquivos,
        Saida saida,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caminhosArquivos);
        var resultados = new List<ResultadoTranscricao>(caminhosArquivos.Count);
        foreach (var caminho in caminhosArquivos)
        {
            resultados.Add(await ProcessarAsync(caminho, saida, progresso, cancellationToken).ConfigureAwait(false));
        }

        return resultados;
    }
}
