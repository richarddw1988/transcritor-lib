namespace Transcritor.Core.Modelos;

public enum EtapaProcessamento
{
    CalculandoHash,
    LendoCache,
    LendoMidia,
    ConvertendoAudio,
    Transcrevendo,
    MontandoFrases,
    CorrigindoTranscricao,
    Traduzindo,
    AnalisandoSentido,
    GravandoResultado,
    Concluido,

    /// <summary>Esperando a vez na GPU: outro processamento de arquivo está com ela (ver <see cref="ProgressoProcessamento.Detalhe"/>).</summary>
    AguardandoGpu,

    /// <summary>Separando a voz da música e dos efeitos (filmes; ver <c>OpcoesArquivo.SepararVoz</c>).</summary>
    SeparandoVoz,
}

/// <summary>Progresso do processamento de um arquivo.</summary>
/// <param name="Etapa">Etapa atual.</param>
/// <param name="Percentual">0 a 100 dentro da etapa; nulo quando a etapa não tem medida (ex.: transcrição no servidor).</param>
/// <param name="DoCache">Verdadeiro quando o resultado veio do cache, sem chamar serviços.</param>
/// <param name="Arquivo">Número do arquivo no lote (1 = o primeiro); 1 fora de lote.</param>
/// <param name="TotalArquivos">Quantos arquivos o lote tem; 1 fora de lote.</param>
/// <param name="Detalhe">Texto extra da etapa (em <see cref="EtapaProcessamento.AguardandoGpu"/>, quem está com a vez).</param>
public sealed record ProgressoProcessamento(
    EtapaProcessamento Etapa, double? Percentual = null, bool DoCache = false, int Arquivo = 1, int TotalArquivos = 1,
    string? Detalhe = null);
