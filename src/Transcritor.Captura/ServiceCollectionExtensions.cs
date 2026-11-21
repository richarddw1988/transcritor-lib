using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Transcritor.Core.Contratos;

namespace Transcritor.Captura;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra a captura de microfone e som do sistema do Windows.</summary>
    public static IServiceCollection AddTranscritorCaptura(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IFabricaFonteAudio, FabricaFonteAudioWindows>();
        return services;
    }
}
