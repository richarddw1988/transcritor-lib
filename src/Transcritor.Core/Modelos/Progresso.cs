namespace Transcritor.Core.Modelos;

public enum EtapaProcessamento
{
    CalculandoHash,
    LendoCache,
    LendoMidia,
    ConvertendoAudio,
    Transcrevendo,
    MontandoFrases,
    Traduzindo,
    GravandoResultado,
    Concluido,
}

/// <summary>Progresso do processamento de um arquivo.</summary>
/// <param name="Etapa">Etapa atual.</param>
/// <param name="Percentual">0 a 100 dentro da etapa; nulo quando a etapa não tem medida (ex.: transcrição no servidor).</param>
/// <param name="DoCache">Verdadeiro quando o resultado veio do cache, sem chamar serviços.</param>
public sealed record ProgressoProcessamento(EtapaProcessamento Etapa, double? Percentual = null, bool DoCache = false);
