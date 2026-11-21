namespace Transcritor.Arquivo.Processos;

public sealed record ResultadoProcesso(int CodigoSaida, string Saida, string Erro);

/// <summary>Executa um programa externo. Existe para trocar o ffmpeg por um fake nos testes.</summary>
public interface IExecutorProcesso
{
    /// <param name="executavel">Programa (nome no PATH ou caminho).</param>
    /// <param name="argumentos">Argumentos, um por item (sem aspas manuais).</param>
    /// <param name="aoLerLinha">Chamado a cada linha da saída padrão, enquanto o processo roda.</param>
    /// <param name="cancellationToken">Cancelamento; mata o processo.</param>
    Task<ResultadoProcesso> ExecutarAsync(
        string executavel,
        IReadOnlyList<string> argumentos,
        Action<string>? aoLerLinha,
        CancellationToken cancellationToken);
}
