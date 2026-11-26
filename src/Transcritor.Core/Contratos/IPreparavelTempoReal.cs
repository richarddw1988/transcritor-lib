namespace Transcritor.Core.Contratos;

/// <summary>
/// Serviço usado no tempo real que pode se adiantar enquanto a sessão abre (ex.: o tradutor liga o contêiner do Ollama
/// e põe na GPU o modelo das frases), para a primeira frase não pagar a subida. O <c>TranscritorTempoReal</c> chama
/// sozinho; falhas são ignoradas (a chamada de verdade mostra o erro).
/// </summary>
public interface IPreparavelTempoReal
{
    /// <summary>Liga o serviço; com <paramref name="carregarModelo"/>, também carrega o modelo do tempo real.</summary>
    /// <param name="carregarModelo">
    /// Põe o modelo na GPU. O <c>TranscritorTempoReal</c> só pede depois de a sessão pegar a vez na GPU, para não
    /// disputar a placa com uma etapa de arquivo em andamento.
    /// </param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task PrepararTempoRealAsync(bool carregarModelo, CancellationToken cancellationToken);
}
