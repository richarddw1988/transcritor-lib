namespace Transcritor.Arquivo.Midia;

/// <summary>Dados levantados pelo ffprobe.</summary>
/// <param name="Duracao">Duração em segundos (0 se o arquivo não informa).</param>
/// <param name="Formato">Nome do contêiner (ex.: "mov,mp4,m4a,3gp,3g2,mj2").</param>
/// <param name="FaixasAudio">Quantidade de faixas de áudio.</param>
public sealed record InfoMidia(double Duracao, string Formato, int FaixasAudio);

/// <summary>Lê dados da mídia e converte o áudio para WAV 16 kHz mono.</summary>
public interface IConversorMidia
{
    Task<InfoMidia> ObterInfoAsync(string caminhoArquivo, CancellationToken cancellationToken);

    /// <param name="caminhoArquivo">Mídia de entrada.</param>
    /// <param name="caminhoWav">WAV de saída (sobrescrito).</param>
    /// <param name="aoAvancar">Recebe os segundos já convertidos.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, Action<double>? aoAvancar, CancellationToken cancellationToken);
}
