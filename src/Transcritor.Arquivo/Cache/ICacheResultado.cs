using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo.Cache;

/// <summary>Guarda resultados já processados, pela chave derivada do hash do arquivo.</summary>
public interface ICacheResultado
{
    Task<ResultadoTranscricao?> LerAsync(string chave, CancellationToken cancellationToken);

    Task GravarAsync(string chave, ResultadoTranscricao resultado, CancellationToken cancellationToken);
}
