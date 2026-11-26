using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core.Processos;
using Transcritor.Arquivo.Separacao;
using Transcritor.Arquivo.Speaches;
using Transcritor.Core.Contratos;

namespace Transcritor.Arquivo;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o módulo de arquivo: <see cref="ITranscritorArquivo"/>, ffmpeg, Speaches e cache em disco.
    /// Registros feitos antes desta chamada (ex.: outro <see cref="IMotorTranscricao"/>) são mantidos.
    /// </summary>
    public static IServiceCollection AddTranscritorArquivo(this IServiceCollection services, Action<OpcoesArquivo>? configurar = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<OpcoesArquivo>();
        if (configurar is not null)
        {
            builder.Configure(configurar);
        }

        services.TryAddSingleton<IExecutorProcesso, ExecutorProcesso>();
        services.TryAddSingleton<ConversorFfmpeg>();
        services.TryAddSingleton<IConversorMidia>(sp => sp.GetRequiredService<ConversorFfmpeg>());
        services.TryAddSingleton<ICacheResultado, CacheEmDisco>();
        services.TryAddSingleton<CoordenadorGpu>();
        services.AddOptions<OpcoesConteineres>();
        services.TryAddSingleton(sp => new GerenciadorConteineres(
            sp.GetRequiredService<IExecutorProcesso>(), sp.GetRequiredService<IOptions<OpcoesConteineres>>().Value));

        services.AddHttpClient<SpeachesMotorTranscricao>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<OpcoesArquivo>>().Value;
            http.BaseAddress = ComBarraFinal(o.SpeachesUrl);
            http.Timeout = o.TimeoutTranscricao;
        }).AddHttpMessageHandler(sp => new ConteinerSobDemanda(
            sp.GetRequiredService<GerenciadorConteineres>(), sp.GetRequiredService<IOptions<OpcoesArquivo>>().Value.ConteinerSpeaches, "health"));
        services.TryAddTransient<IMotorTranscricao>(sp => sp.GetRequiredService<SpeachesMotorTranscricao>());
        services.AddHttpClient<SeparadorDemucs>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<OpcoesArquivo>>().Value;
            http.BaseAddress = ComBarraFinal(o.SeparadorUrl);
            http.Timeout = o.TimeoutSeparacao;
        }).AddHttpMessageHandler(sp => new ConteinerSobDemanda(
            sp.GetRequiredService<GerenciadorConteineres>(), sp.GetRequiredService<IOptions<OpcoesArquivo>>().Value.ConteinerSeparador, "health"));
        services.TryAddTransient<ISeparadorVoz>(sp => sp.GetRequiredService<SeparadorDemucs>());
        services.TryAddTransient<ITranscritorArquivo, TranscritorArquivo>();

        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<ConversorFfmpeg>());
        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<SpeachesMotorTranscricao>());
        services.AddTransient<IServicoVerificavel>(sp => sp.GetRequiredService<SeparadorDemucs>());
        return services;
    }

    private static Uri ComBarraFinal(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
