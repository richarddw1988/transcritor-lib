namespace Transcritor.Core.Contratos;

/// <summary>Frase a traduzir com as vizinhas como contexto (traduz-se só <see cref="Texto"/>).</summary>
/// <param name="Texto">Frase a traduzir.</param>
/// <param name="Origem">Idioma da frase ("pt" ou "en").</param>
/// <param name="Destino">Idioma da tradução ("pt" ou "en").</param>
/// <param name="Anteriores">Frases que vêm antes, em ordem (contexto).</param>
/// <param name="Seguintes">Frases que vêm depois, em ordem (contexto).</param>
public sealed record PedidoTraducaoFrase(
    string Texto,
    string Origem,
    string Destino,
    IReadOnlyList<string> Anteriores,
    IReadOnlyList<string> Seguintes);

/// <summary>Palavra solta, traduzida como ela é usada na frase.</summary>
/// <param name="Palavra">Palavra a traduzir (quem escolhe é o app que usa a lib).</param>
/// <param name="Frase">Frase de onde a palavra veio (contexto obrigatório).</param>
/// <param name="Origem">Idioma da frase.</param>
/// <param name="Destino">Idioma da tradução.</param>
/// <param name="TraducaoDaFrase">
/// Tradução já pronta da frase (ex.: o campo "traducao" do JSON). É a melhor pista do sentido da palavra;
/// se vier nula, o tradutor traduz a frase antes.
/// </param>
public sealed record PedidoTraducaoPalavra(string Palavra, string Frase, string Origem, string Destino, string? TraducaoDaFrase = null);

/// <summary>Tradução português ↔ inglês pelo sentido.</summary>
public interface ITradutor
{
    /// <summary>Identifica motor, modelos e versão do prompt; entra na chave do cache.</summary>
    string Identificador { get; }

    /// <summary>Traduz uma frase usando as vizinhas informadas como contexto.</summary>
    Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken);

    /// <summary>
    /// Traduz uma sequência de frases (ex.: todas as frases de um vídeo), cada uma com
    /// as vizinhas como contexto. Devolve uma tradução por frase, na mesma ordem.
    /// </summary>
    /// <param name="frases">Frases em ordem.</param>
    /// <param name="origem">Idioma das frases.</param>
    /// <param name="destino">Idioma da tradução.</param>
    /// <param name="progresso">Recebe o percentual concluído (0 a 100).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task<IReadOnlyList<string>> TraduzirFrasesAsync(
        IReadOnlyList<string> frases,
        string origem,
        string destino,
        IProgress<double>? progresso,
        CancellationToken cancellationToken);

    /// <summary>Traduz uma palavra solta com a frase dela como contexto (com cache).</summary>
    Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken);
}
