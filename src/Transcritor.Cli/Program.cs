using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Transcritor.Arquivo;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Traducao;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

// ---- arquivo ---------------------------------------------------------------
var argCaminho = new Argument<FileInfo>("caminho") { Description = "Áudio ou vídeo (mp4, mp3, mkv, wav, ...)." };
var optModelo = new Option<string?>("--modelo") { Description = "Modelo do Speaches (padrão: appsettings.json)." };
var optIdioma = new Option<string?>("--idioma") { Description = "Força o idioma falado: pt ou en (padrão: detecção automática)." };
var optJson = new Option<FileInfo?>("--json") { Description = "Também grava o JSON neste caminho." };
var optSaida = new Option<Saida>("--saida")
{
    Description = "Portugues, Ingles ou Ambos. O texto fica no idioma falado; a tradução vem quando pedida.",
    DefaultValueFactory = _ => Saida.Ambos,
};

var cmdArquivo = new Command("arquivo", "Transcreve (e traduz) um arquivo e grava o JSON com frases e palavras.")
{
    argCaminho, optSaida, optModelo, optIdioma, optJson,
};
cmdArquivo.SetAction(async (resultado, ct) =>
{
    var caminho = resultado.GetValue(argCaminho)!;
    var saida = resultado.GetValue(optSaida);
    var modelo = resultado.GetValue(optModelo);
    var idioma = resultado.GetValue(optIdioma);
    var json = resultado.GetValue(optJson);

    using var sp = CriarServicos(o =>
    {
        if (!string.IsNullOrWhiteSpace(modelo)) o.ModeloTranscricao = modelo;
        if (!string.IsNullOrWhiteSpace(idioma)) o.Idioma = idioma;
    });

    return await Executar(async () =>
    {
        var transcritor = sp.GetRequiredService<ITranscritorArquivo>();
        var relogio = Stopwatch.StartNew();
        var progresso = new ProgressoConsole();
        var r = await transcritor.ProcessarAsync(caminho.FullName, saida, progresso, ct);
        relogio.Stop();

        if (json is not null)
        {
            await Transcritor.Core.JsonTranscricao.GravarAsync(r, json.FullName, ct);
        }

        Console.WriteLine();
        Console.WriteLine($"Idioma:   {r.Arquivo.Idioma}");
        Console.WriteLine($"Duração:  {TimeSpan.FromSeconds(r.Arquivo.Duracao):hh\\:mm\\:ss\\.f}");
        Console.WriteLine($"Frases:   {r.Frases.Count}  |  Palavras: {r.Frases.Sum(f => f.Palavras.Count)}");
        Console.WriteLine($"Hash:     {r.Arquivo.Hash}");
        Console.WriteLine($"Tempo:    {relogio.Elapsed.TotalSeconds:F1} s{(progresso.DoCache ? "  (do cache, nenhum serviço chamado)" : string.Empty)}");
        Console.WriteLine($"JSON:     {OpcoesArquivo.CaminhoJsonAoLado(caminho.FullName)}");
        if (json is not null) Console.WriteLine($"          {json.FullName}");

        Console.WriteLine();
        foreach (var f in r.Frases.Take(5))
        {
            Console.WriteLine($"  [{f.Inicio,8:F2} → {f.Fim,8:F2}] {f.Texto}");
            if (f.Traducao is not null) Console.WriteLine($"  {"",21} {f.Traducao}");
        }

        if (r.Frases.Count > 5) Console.WriteLine($"  ... mais {r.Frases.Count - 5} frases");
        return 0;
    });
});

// ---- traduzir --------------------------------------------------------------
var argTexto = new Argument<string>("texto") { Description = "Frase a traduzir." };
var optDe = new Option<string>("--de") { Description = "Idioma de origem: pt ou en.", Required = true };
var optPara = new Option<string>("--para") { Description = "Idioma de destino: pt ou en.", Required = true };
var optAntes = new Option<string[]>("--antes") { Description = "Frase anterior (contexto). Pode repetir.", AllowMultipleArgumentsPerToken = false };
var optDepois = new Option<string[]>("--depois") { Description = "Frase seguinte (contexto). Pode repetir." };

var cmdTraduzir = new Command("traduzir", "Traduz uma frase pelo sentido, com frases vizinhas opcionais como contexto.")
{
    argTexto, optDe, optPara, optAntes, optDepois,
};
cmdTraduzir.SetAction(async (resultado, ct) =>
{
    using var sp = CriarServicos(null);
    return await Executar(async () =>
    {
        var pedido = new PedidoTraducaoFrase(
            resultado.GetValue(argTexto)!,
            resultado.GetValue(optDe)!,
            resultado.GetValue(optPara)!,
            resultado.GetValue(optAntes) ?? [],
            resultado.GetValue(optDepois) ?? []);
        var relogio = Stopwatch.StartNew();
        var traducao = await sp.GetRequiredService<ITradutor>().TraduzirFraseAsync(pedido, ct);
        Console.WriteLine(traducao);
        Console.Error.WriteLine($"({relogio.Elapsed.TotalSeconds:F1} s)");
        return 0;
    });
});

// ---- palavra ---------------------------------------------------------------
var argPalavra = new Argument<string>("palavra") { Description = "Palavra a traduzir." };
var optFrase = new Option<string>("--frase") { Description = "Frase de onde a palavra veio.", Required = true };
var optTraducaoFrase = new Option<string?>("--traducao-frase") { Description = "Tradução da frase, se já existir (ex.: do JSON)." };

var cmdPalavra = new Command("palavra", "Traduz uma palavra como ela é usada na frase (com cache).")
{
    argPalavra, optFrase, optDe, optPara, optTraducaoFrase,
};
cmdPalavra.SetAction(async (resultado, ct) =>
{
    using var sp = CriarServicos(null);
    return await Executar(async () =>
    {
        var pedido = new PedidoTraducaoPalavra(
            resultado.GetValue(argPalavra)!,
            resultado.GetValue(optFrase)!,
            resultado.GetValue(optDe)!,
            resultado.GetValue(optPara)!,
            resultado.GetValue(optTraducaoFrase));
        var relogio = Stopwatch.StartNew();
        var traducao = await sp.GetRequiredService<ITradutor>().TraduzirPalavraAsync(pedido, ct);
        Console.WriteLine(traducao);
        Console.Error.WriteLine($"({relogio.Elapsed.TotalSeconds:F1} s)");
        return 0;
    });
});

// ---- servicos --------------------------------------------------------------
var cmdServicos = new Command("servicos", "Verifica se ffmpeg, Speaches e Ollama (com os modelos) estão disponíveis.");
cmdServicos.SetAction(async (_, ct) =>
{
    using var sp = CriarServicos(null);
    var falhas = 0;
    foreach (var servico in sp.GetServices<IServicoVerificavel>())
    {
        try
        {
            await servico.VerificarAsync(ct);
            Console.WriteLine($"[ OK  ] {servico.NomeServico}");
        }
        catch (TranscritorException ex)
        {
            falhas++;
            Console.WriteLine($"[FALHA] {servico.NomeServico}: {ex.Message}");
        }
    }

    return falhas == 0 ? 0 : 1;
});

var raiz = new RootCommand("Cli de teste da Transcritor lib.") { cmdArquivo, cmdTraduzir, cmdPalavra, cmdServicos };
return await raiz.Parse(args).InvokeAsync();

// ---- apoio -----------------------------------------------------------------
static ServiceProvider CriarServicos(Action<OpcoesArquivo>? ajustar)
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables("TRANSCRITOR_")
        .Build();

    var services = new ServiceCollection();
    services.AddTranscritorArquivo();
    services.AddTranscritorTraducao();
    services.Configure<OpcoesArquivo>(config.GetSection("Transcritor:Arquivo"));
    services.Configure<OpcoesTraducao>(config.GetSection("Transcritor:Traducao"));
    if (ajustar is not null)
    {
        services.PostConfigure(ajustar);
    }

    return services.BuildServiceProvider();
}

static async Task<int> Executar(Func<Task<int>> acao)
{
    try
    {
        return await acao();
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelado.");
        return 130;
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Erro: {ex.Message} ({ex.FileName})");
        return 2;
    }
    catch (TranscritorException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Erro ({ex.GetType().Name}): {ex.Message}");
        return 1;
    }
}

/// <summary>Mostra o progresso na mesma thread que reporta (sem SynchronizationContext).</summary>
internal sealed class ProgressoConsole : IProgress<ProgressoProcessamento>
{
    private EtapaProcessamento? _etapa;
    private int _ultimoDecimo = -1;

    public bool DoCache { get; private set; }

    public void Report(ProgressoProcessamento value)
    {
        DoCache |= value.DoCache;
        var decimo = value.Percentual is { } p ? (int)(p / 10) : -1;
        if (value.Etapa == _etapa && decimo == _ultimoDecimo)
        {
            return;
        }

        if (value.Etapa != _etapa && _etapa is not null)
        {
            Console.WriteLine();
        }

        _etapa = value.Etapa;
        _ultimoDecimo = decimo;
        var percentual = value.Percentual is { } q ? $" {q,5:F1}%" : string.Empty;
        Console.Write($"\r{value.Etapa,-18}{percentual}");
    }
}
