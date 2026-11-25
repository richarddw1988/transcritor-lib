using Transcritor.Core.Erros;

namespace Transcritor.Core.Processos;

/// <summary>Liga, para e consulta um contêiner pelo docker. Sem docker na máquina, não faz nada (devolve nulo/falso).</summary>
/// <param name="executor">Executor de processos.</param>
/// <param name="caminhoDocker">Executável do docker (nome no PATH ou caminho).</param>
/// <param name="nome">Nome do contêiner.</param>
public sealed class ConteinerDocker(IExecutorProcesso executor, string caminhoDocker, string nome)
{
    public string Nome => nome;

    /// <summary>true no ar, false parado, nulo se não há docker ou o contêiner não existe.</summary>
    public async Task<bool?> NoArAsync(CancellationToken cancellationToken)
    {
        var r = await DockerAsync(["inspect", "-f", "{{.State.Running}}", nome], cancellationToken).ConfigureAwait(false);
        return r is { CodigoSaida: 0 } ? r.Saida.Trim() == "true" : null;
    }

    /// <summary>Para o contêiner; nulo se não há docker.</summary>
    public Task<ResultadoProcesso?> PararAsync(CancellationToken cancellationToken) =>
        DockerAsync(["stop", nome], cancellationToken);

    /// <summary>Liga o contêiner; nulo se não há docker.</summary>
    public Task<ResultadoProcesso?> LigarAsync(CancellationToken cancellationToken) =>
        DockerAsync(["start", nome], cancellationToken);

    private async Task<ResultadoProcesso?> DockerAsync(IReadOnlyList<string> argumentos, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            return null;
        }

        try
        {
            return await executor.ExecutarAsync(caminhoDocker, argumentos, null, cancellationToken).ConfigureAwait(false);
        }
        catch (FerramentaExternaException)
        {
            return null;
        }
    }
}
