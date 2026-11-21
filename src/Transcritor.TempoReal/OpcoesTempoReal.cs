namespace Transcritor.TempoReal;

/// <summary>Configuração do tempo real. Seção sugerida: "Transcritor:TempoReal".</summary>
public sealed class OpcoesTempoReal
{
    /// <summary>Endpoint WebSocket do WhisperLiveKit.</summary>
    public Uri WhisperLiveKitUrl { get; set; } = new("ws://localhost:8001/asr");

    /// <summary>Tempo máximo para conectar.</summary>
    public TimeSpan TimeoutConexao { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Depois que o áudio termina, quanto esperar o servidor entregar o resto ("ready_to_stop")
    /// e as últimas traduções.
    /// </summary>
    public TimeSpan TimeoutFinalizacao { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Quantos trechos confirmados anteriores vão como contexto da tradução.</summary>
    public int TrechosContexto { get; set; } = 2;
}
