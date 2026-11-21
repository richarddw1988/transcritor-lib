using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Transcritor.Core.Contratos;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra <see cref="ITradutor"/> (Ollama) e o cache de palavras em disco.</summary>
    public static IServiceCollection AddTranscritorTraducao(this IServiceCollection services, Action<OpcoesTraducao>? configurar = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<OpcoesTraducao>();
        if (configurar is not null)
        {
            builder.Configure(configurar);
        }

        services.AddHttpClient<ClienteOllama>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<OpcoesTraducao>>().Value;
            http.BaseAddress = o.OllamaUrl.AbsoluteUri.EndsWith('/') ? o.OllamaUrl : new Uri(o.OllamaUrl.AbsoluteUri + "/");
            http.Timeout = o.Timeout;
        });
        services.TryAddSingleton<ICachePalavras, CachePalavrasEmDisco>();
        services.TryAddTransient<TradutorOllama>();
        services.TryAddTransient<ITradutor>(sp => sp.GetRequiredService<TradutorOllama>());
        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<TradutorOllama>());

        services.TryAddTransient<Analise.AnalisadorOllama>();
        services.TryAddTransient<IAnalisadorSentido>(sp => sp.GetRequiredService<Analise.AnalisadorOllama>());
        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<Analise.AnalisadorOllama>());
        return services;
    }
}
