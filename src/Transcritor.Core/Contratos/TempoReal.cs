namespace Transcritor.Core.Contratos;

public enum TipoEvento
{
    /// <summary>Texto ainda em reconhecimento; pode mudar ou sumir no próximo evento.</summary>
    Provisorio,

    /// <summary>Trecho final. Não muda mais, exceto para ganhar a tradução (mesmo <see cref="EventoTempoReal.Id"/>).</summary>
    Confirmado,
}

/// <summary>Evento do tempo real.</summary>
/// <param name="Tipo">Provisório ou confirmado.</param>
/// <param name="Id">
/// Sequência do trecho. Cada Confirmado tem um Id novo; quando a tradução fica pronta, o mesmo trecho é
/// emitido de novo como Confirmado com o mesmo Id e <see cref="Traducao"/> preenchida.
/// O Provisorio usa o Id que o próximo Confirmado vai receber.
/// </param>
/// <param name="Texto">Texto no idioma falado. Num Provisorio vazio, significa "não há texto pendente".</param>
/// <param name="Traducao">Tradução, quando a <see cref="Saida"/> pede e já está pronta.</param>
/// <param name="Idioma">Idioma falado ("pt", "en"), se conhecido.</param>
/// <param name="Inicio">Segundos desde o início da sessão (aproximado).</param>
/// <param name="Fim">Segundos desde o início da sessão (aproximado).</param>
public sealed record EventoTempoReal(
    TipoEvento Tipo,
    long Id,
    string Texto,
    string? Traducao,
    string? Idioma,
    double Inicio,
    double Fim);

/// <summary>Opções de uma sessão de tempo real.</summary>
/// <param name="Idioma">Força o idioma falado ("pt" ou "en"); nulo = detecção automática.</param>
public sealed record OpcoesSessaoTempoReal(string? Idioma = null);

/// <summary>
/// Motor de reconhecimento em tempo real (ex.: WhisperLiveKit). Recebe PCM s16le 16 kHz mono e emite eventos
/// sem tradução. Terminar a sequência de áudio encerra a sessão com calma (o motor entrega o que falta);
/// cancelar o token aborta.
/// </summary>
public interface IMotorTempoReal
{
    IAsyncEnumerable<EventoTempoReal> TranscreverAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        OpcoesSessaoTempoReal sessao,
        CancellationToken cancellationToken);
}

/// <summary>Fonte de áudio que entrega PCM s16le, 16 kHz, mono.</summary>
public interface IFonteAudio : IAsyncDisposable
{
    /// <summary>Lê o áudio. Cancelar o token encerra a sequência normalmente (sem exceção).</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> CapturarAsync(CancellationToken cancellationToken);
}

/// <summary>Cria a fonte de áudio para cada <see cref="Fonte"/> (a implementação depende do sistema).</summary>
public interface IFabricaFonteAudio
{
    IFonteAudio Criar(Fonte fonte);
}

/// <summary>Tempo real completo: captura + reconhecimento + tradução conforme a <see cref="Saida"/>.</summary>
public interface ITranscritorTempoReal
{
    /// <summary>Inicia uma sessão.</summary>
    /// <param name="fonte">Microfone ou som do sistema.</param>
    /// <param name="saida">Idiomas pedidos; a tradução chega num Confirmado reemitido com o mesmo Id.</param>
    /// <param name="idioma">Força o idioma falado; nulo = detecção automática.</param>
    /// <param name="cancellationToken">
    /// Para a captura. A sessão então termina com calma: os últimos trechos são confirmados e traduzidos
    /// (com limite de tempo) e a sequência acaba sem exceção.
    /// </param>
    IAsyncEnumerable<EventoTempoReal> IniciarAsync(Fonte fonte, Saida saida, string? idioma = null, CancellationToken cancellationToken = default);

    /// <summary>Mesmo que <see cref="IniciarAsync(Fonte, Saida, string?, CancellationToken)"/>, com uma fonte de áudio qualquer.</summary>
    IAsyncEnumerable<EventoTempoReal> IniciarAsync(IFonteAudio fonte, Saida saida, string? idioma = null, CancellationToken cancellationToken = default);
}
