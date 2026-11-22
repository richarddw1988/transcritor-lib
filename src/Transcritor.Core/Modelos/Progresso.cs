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
}

/// <summary>Progresso do processamento de um arquivo.</summary>
/// <param name="Etapa">Etapa atual.</param>
/// <param name="Percentual">0 a 100 dentro da etapa; nulo quando a etapa não tem medida (ex.: transcrição no servidor).</param>
/// <param name="DoCache">Verdadeiro quando o resultado veio do cache, sem chamar serviços.</param>
/// <param name="Arquivo">Número do arquivo no lote (1 = o primeiro); 1 fora de lote.</param>
/// <param name="TotalArquivos">Quantos arquivos o lote tem; 1 fora de lote.</param>
public sealed record ProgressoProcessamento(
    EtapaProcessamento Etapa, double? Percentual = null, bool DoCache = false, int Arquivo = 1, int TotalArquivos = 1);
