using Transcritor.Core.Modelos;

namespace Transcritor.Core.Contratos;

/// <summary>Frase a analisar, já traduzida.</summary>
/// <param name="Palavras">Palavras da frase, na ordem (os índices dos blocos apontam para esta lista).</param>
/// <param name="Traducao">Tradução da frase.</param>
/// <param name="Origem">Idioma da frase.</param>
/// <param name="Destino">Idioma da tradução (e das notas e do sentido).</param>
/// <param name="Anteriores">Frases anteriores (contexto).</param>
/// <param name="Seguintes">Frases seguintes (contexto).</param>
public sealed record PedidoAnaliseFrase(
    IReadOnlyList<string> Palavras,
    string Traducao,
    string Origem,
    string Destino,
    IReadOnlyList<string> Anteriores,
    IReadOnlyList<string> Seguintes);

/// <summary>Resultado da análise: o que a pessoa quis dizer e as unidades de sentido.</summary>
public sealed record AnaliseFrase(string? Sentido, IReadOnlyList<Bloco> Blocos);

/// <summary>
/// Explica uma frase para quem está aprendendo: divide em unidades de sentido (expressões, phrasal verbs,
/// colocações, nomes) alinhadas às palavras e à tradução, e diz o que a pessoa quis dizer.
/// </summary>
public interface IAnalisadorSentido
{
    /// <summary>Identifica modelo e versão do prompt; entra na chave do cache.</summary>
    string Identificador { get; }

    /// <summary>Falso quando a análise está desligada na configuração; quem usa deve pular a análise.</summary>
    bool Ativo { get; }

    Task<AnaliseFrase> AnalisarAsync(PedidoAnaliseFrase pedido, CancellationToken cancellationToken);
}
