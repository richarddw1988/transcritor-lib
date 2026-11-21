using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Transcritor.Core.Contratos;
using Transcritor.TempoReal.WebSockets;
using Transcritor.TempoReal.WhisperLiveKit;

namespace Transcritor.TempoReal;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="ITranscritorTempoReal"/> e o motor WhisperLiveKit. Para microfone e som do sistema,
    /// registre também uma <see cref="IFabricaFonteAudio"/> (AddTranscritorCaptura no Windows); para traduzir,
    /// AddTranscritorTraducao.
    /// </summary>
    public static IServiceCollection AddTranscritorTempoReal(this IServiceCollection services, Action<OpcoesTempoReal>? configurar = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<OpcoesTempoReal>();
        if (configurar is not null)
        {
            builder.Configure(configurar);
        }

        services.TryAddSingleton<IFabricaConexaoWebSocket, FabricaClientWebSocket>();
        services.TryAddTransient<MotorWhisperLiveKit>();
        services.TryAddTransient<IMotorTempoReal>(sp => sp.GetRequiredService<MotorWhisperLiveKit>());
        services.TryAddTransient<ITranscritorTempoReal, TranscritorTempoReal>();
        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<MotorWhisperLiveKit>());
        return services;
    }
}
