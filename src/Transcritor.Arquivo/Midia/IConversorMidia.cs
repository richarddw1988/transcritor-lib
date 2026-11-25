namespace Transcritor.Arquivo.Midia;

/// <summary>Dados levantados pelo ffprobe.</summary>
/// <param name="Duracao">Duração em segundos (0 se o arquivo não informa).</param>
/// <param name="Formato">Nome do contêiner (ex.: "mov,mp4,m4a,3gp,3g2,mj2").</param>
/// <param name="FaixasAudio">Quantidade de faixas de áudio.</param>
/// <param name="IdiomasFaixas">
/// Idioma de cada faixa de áudio, na ordem ("pt", "en", outro código, ou vazio quando o arquivo não diz); nulo quando
/// o conversor não informa.
/// </param>
/// <param name="LayoutsFaixas">Layout de canais de cada faixa ("5.1", "stereo"…, ou vazio); nulo quando o conversor não informa.</param>
public sealed record InfoMidia(
    double Duracao, string Formato, int FaixasAudio, IReadOnlyList<string>? IdiomasFaixas = null, IReadOnlyList<string>? LayoutsFaixas = null);

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

    /// <summary>
    /// O mesmo, com outra taxa de amostragem (o separador de voz trabalha em 44,1 kHz). A implementação padrão só
    /// sabe 16 kHz.
    /// </summary>
    /// <param name="caminhoArquivo">Mídia de entrada.</param>
    /// <param name="caminhoWav">WAV de saída (sobrescrito).</param>
    /// <param name="faixa">Índice da faixa entre as de áudio.</param>
    /// <param name="taxa">Amostras por segundo.</param>
    /// <param name="aoAvancar">Recebe os segundos já convertidos.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task ConverterParaWavAsync(
        string caminhoArquivo, string caminhoWav, int faixa, int taxa, Action<double>? aoAvancar, CancellationToken cancellationToken) =>
        taxa == 16000
            ? ConverterParaWavAsync(caminhoArquivo, caminhoWav, faixa, aoAvancar, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} só converte para 16 kHz.");
}

/// <summary>Layouts de canais de áudio.</summary>
public static class CanaisAudio
{
    /// <summary>Layouts do ffmpeg que têm o canal central (FC). Mono não conta: o canal único já é tudo.</summary>
    private static readonly HashSet<string> LayoutsComCentral = new(StringComparer.OrdinalIgnoreCase)
    {
        "3.0", "3.0(back)", "3.1", "4.0", "4.1", "5.0", "5.0(side)", "5.1", "5.1(side)", "6.0", "6.1", "6.1(back)",
        "7.0", "7.0(front)", "7.1", "7.1(wide)", "7.1(wide-side)", "7.1(top)", "5.1.2", "5.1.4", "7.1.2", "7.1.4",
        "7.2.3", "9.1.4", "hexagonal", "octagonal", "22.2",
    };

    /// <summary>O layout tem canal central, onde fica o diálogo nos filmes.</summary>
    public static bool TemCanalCentral(string? layout) => layout is not null && LayoutsComCentral.Contains(layout.Trim());
}
