namespace Transcritor.Core.Contratos;

/// <summary>Frase como o reconhecimento de fala ouviu, a corrigir.</summary>
/// <param name="Palavras">Palavras da frase, na ordem e com a pontuação colada (os índices das correções apontam para esta lista).</param>
/// <param name="Idioma">Idioma falado ("pt", "en").</param>
/// <param name="Anteriores">Frases anteriores (contexto).</param>
/// <param name="Seguintes">Frases seguintes (contexto).</param>
/// <param name="Confiancas">
/// Confiança de cada palavra (mesma ordem de <paramref name="Palavras"/>, ver <see cref="Modelos.Palavra.Confianca"/>).
/// Só palavras abaixo do limite configurado podem ser trocadas; nulo (ou confiança nula) = qualquer palavra pode.
/// </param>
public sealed record PedidoCorrecao(
    IReadOnlyList<string> Palavras,
    string Idioma,
    IReadOnlyList<string> Anteriores,
    IReadOnlyList<string> Seguintes,
    IReadOnlyList<double?>? Confiancas = null);

/// <summary>Palavra trocada pela correção.</summary>
/// <param name="Indice">Posição da palavra em <see cref="PedidoCorrecao.Palavras"/>.</param>
/// <param name="Ouvida">Palavra como foi reconhecida.</param>
/// <param name="Corrigida">Palavra corrigida, com a mesma pontuação colada.</param>
public sealed record Correcao(int Indice, string Ouvida, string Corrigida);

/// <summary>
/// Corrige palavras que o reconhecimento de fala ouviu errado ("night" no lugar de "knight", "cela" no lugar de
/// "sela"), pelo sentido da frase. Só troca uma palavra por outra de som parecido; não reescreve a frase.
/// </summary>
public interface ICorretorTranscricao
{
    /// <summary>Identifica modelo e versão do prompt; entra na chave do cache.</summary>
    string Identificador { get; }

    /// <summary>Falso quando a correção está desligada na configuração; quem usa deve pular a correção.</summary>
    bool Ativo { get; }

    /// <summary>Trocas a fazer, em ordem de índice; vazio se a frase parece certa.</summary>
    Task<IReadOnlyList<Correcao>> CorrigirAsync(PedidoCorrecao pedido, CancellationToken cancellationToken);
}
