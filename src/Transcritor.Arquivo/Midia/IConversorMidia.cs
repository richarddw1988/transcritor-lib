namespace Transcritor.Arquivo.Midia;

/// <summary>Dados levantados pelo ffprobe.</summary>
/// <param name="Duracao">Duração em segundos (0 se o arquivo não informa).</param>
/// <param name="Formato">Nome do contêiner (ex.: "mov,mp4,m4a,3gp,3g2,mj2").</param>
/// <param name="FaixasAudio">Quantidade de faixas de áudio.</param>
/// <param name="IdiomasFaixas">
/// Idioma de cada faixa de áudio, na ordem ("pt", "en", outro código, ou vazio quando o arquivo não diz); nulo quando
/// o conversor não informa.
/// </param>
public sealed record InfoMidia(double Duracao, string Formato, int FaixasAudio, IReadOnlyList<string>? IdiomasFaixas = null);

/// <summary>Lê dados da mídia e converte o áudio para WAV 16 kHz mono.</summary>
public interface IConversorMidia
{
    Task<InfoMidia> ObterInfoAsync(string caminhoArquivo, CancellationToken cancellationToken);

    /// <param name="caminhoArquivo">Mídia de entrada.</param>
    /// <param name="caminhoWav">WAV de saída (sobrescrito).</param>
    /// <param name="aoAvancar">Recebe os segundos já convertidos.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, Action<double>? aoAvancar, CancellationToken cancellationToken);

    /// <summary>
    /// Converte uma faixa de áudio escolhida (0 = a primeira), para arquivos com mais de uma (filme dublado). A
    /// implementação padrão só sabe a primeira.
    /// </summary>
    /// <param name="caminhoArquivo">Mídia de entrada.</param>
    /// <param name="caminhoWav">WAV de saída (sobrescrito).</param>
    /// <param name="faixa">Índice da faixa entre as de áudio.</param>
    /// <param name="aoAvancar">Recebe os segundos já convertidos.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, int faixa, Action<double>? aoAvancar, CancellationToken cancellationToken) =>
        faixa == 0
            ? ConverterParaWavAsync(caminhoArquivo, caminhoWav, aoAvancar, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} só converte a primeira faixa de áudio.");
}
