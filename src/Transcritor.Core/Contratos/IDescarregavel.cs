namespace Transcritor.Core.Contratos;

/// <summary>
/// Serviço que carrega um modelo na GPU e sabe tirá-lo de lá, para o modelo da próxima etapa caber inteiro
/// (ver <c>OpcoesArquivo.DescarregarModelosEntreEtapas</c>).
/// </summary>
public interface IDescarregavel
{
    /// <summary>
    /// Tira da GPU os modelos que este serviço usa e volta quando saíram. Não lança exceção se o serviço não responder
    /// (o modelo sai sozinho depois do tempo ocioso do serviço); só o cancelamento interrompe.
    /// </summary>
    Task DescarregarAsync(CancellationToken cancellationToken);
}
