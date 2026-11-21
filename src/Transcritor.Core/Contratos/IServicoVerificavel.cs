namespace Transcritor.Core.Contratos;

/// <summary>Dependência externa que sabe verificar se está disponível.</summary>
public interface IServicoVerificavel
{
    /// <summary>Nome mostrado ao usuário (ex.: "Speaches", "ffmpeg").</summary>
    string NomeServico { get; }

    /// <summary>Lança <see cref="Erros.TranscritorException"/> com mensagem clara se o serviço não estiver pronto.</summary>
    Task VerificarAsync(CancellationToken cancellationToken);
}
