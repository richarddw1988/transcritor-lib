using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Transcritor.Arquivo;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Captura;
using Transcritor.TempoReal;
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

var optSentido = new Option<bool>("--sentido") { Description = "Também gera blocos (expressões) e o que quis dizer em cada frase (lento: ~13 s/frase com gemma3:12b)." };

var cmdArquivo = new Command("arquivo", "Transcreve (e traduz) um arquivo e grava o JSON com frases e palavras.")
{
    argCaminho, optSaida, optModelo, optIdioma, optJson, optSentido,
};
cmdArquivo.SetAction(async (resultado, ct) =>
{
    var caminho = resultado.GetValue(argCaminho)!;
    var saida = resultado.GetValue(optSaida);
    var modelo = resultado.GetValue(optModelo);
    var idioma = resultado.GetValue(optIdioma);
    var json = resultado.GetValue(optJson);
    var sentido = resultado.GetValue(optSentido);

    using var sp = CriarServicos(
        o =>
        {
            if (!string.IsNullOrWhiteSpace(modelo)) o.ModeloTranscricao = modelo;
            if (!string.IsNullOrWhiteSpace(idioma)) o.Idioma = idioma;
        },
        t =>
        {
            if (sentido) t.AnalisarSentido = true;
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
            if (f.Sentido is not null) Console.WriteLine($"  {"",21} sentido: {f.Sentido}");
            foreach (var b in (f.Blocos ?? []).Where(b => b.Tipo != TipoBloco.Literal || b.Nota is not null))
            {
                Console.WriteLine($"  {"",21} [{b.Tipo}] {b.Texto} → {b.Traducao}{(b.Nota is null ? "" : "  (" + b.Nota + ")")}");
            }
        }

        if (r.Frases.Count > 5) Console.WriteLine($"  ... mais {r.Frases.Count - 5} frases");

        var comBlocos = r.Frases.Where(f => f.Blocos is not null).ToList();
        if (comBlocos.Count > 0)
        {
            var blocos = comBlocos.SelectMany(f => f.Blocos!).ToList();
            var cobertas = comBlocos.Sum(f => f.Blocos!.Sum(b => b.PalavraFinal - b.PalavraInicial + 1));
            Console.WriteLine();
            Console.WriteLine($"Análise:  {blocos.Count} blocos ({blocos.Count(b => b.Tipo != TipoBloco.Literal)} expressões/phrasal/colocações/nomes, " +
                $"{blocos.Count(b => b.Nota is not null)} com nota); palavras cobertas {cobertas}/{comBlocos.Sum(f => f.Palavras.Count)}; " +
                $"tradução do bloco achada na frase {blocos.Count(b => comBlocos.Any(f => f.Blocos!.Contains(b) && f.Traducao!.Contains(b.Traducao, StringComparison.OrdinalIgnoreCase)))}/{blocos.Count}");
        }

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
var cmdServicos = new Command("servicos", "Verifica ffmpeg, Speaches, Ollama (com os modelos) e WhisperLiveKit.");
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

// ---- tempo real: microfone, sistema, simular ------------------------------------
var optSaidaTr = new Option<Saida>("--saida")
{
    Description = "Portugues, Ingles ou Ambos.",
    DefaultValueFactory = _ => Saida.Ambos,
};
var optIdiomaTr = new Option<string?>("--idioma") { Description = "Força o idioma falado: pt ou en (padrão: detecção automática)." };
var optContexto = new Option<string?>("--contexto") { Description = "Nomes e vocabulário esperados, para ajudar o reconhecimento (até 1000 caracteres)." };
OpcoesSessaoTempoReal Sessao(System.CommandLine.ParseResult r) => new(r.GetValue(optIdiomaTr), r.GetValue(optContexto));

Command ComandoCaptura(string nome, Fonte fonte, string descricao)
{
    var cmd = new Command(nome, descricao + " Ctrl+C encerra.") { optSaidaTr, optIdiomaTr, optContexto };
    cmd.SetAction(async (resultado, ct) =>
    {
        using var sp = CriarServicos(null);
        return await Executar(async () =>
        {
            var dispositivo = new FonteAudioWasapi(fonte).NomeDispositivo();
            Console.Error.WriteLine($"{fonte}: {dispositivo}. Fale/toque algo; Ctrl+C encerra.");
            var transcritor = sp.GetRequiredService<ITranscritorTempoReal>();
            await MostrarEventos(transcritor.IniciarAsync(fonte, resultado.GetValue(optSaidaTr), Sessao(resultado), ct));
            return 0;
        });
    });
    return cmd;
}

var cmdMicrofone = ComandoCaptura("microfone", Fonte.Microfone, "Transcreve o microfone em tempo real.");
var cmdSistema = ComandoCaptura("sistema", Fonte.SomDoSistema, "Transcreve o som que está tocando no computador.");

var argMidia = new Argument<FileInfo>("caminho") { Description = "Áudio ou vídeo; é tocado para o WhisperLiveKit no ritmo real." };
var optVelocidade = new Option<double>("--velocidade") { Description = "1 = tempo real (padrão), 2 = dobro.", DefaultValueFactory = _ => 1 };
var cmdSimular = new Command("simular", "Envia um arquivo ao tempo real como se fosse o microfone (teste sem falar).")
{
    argMidia, optSaidaTr, optIdiomaTr, optContexto, optVelocidade,
};
cmdSimular.SetAction(async (resultado, ct) =>
{
    using var sp = CriarServicos(null);
    return await Executar(async () =>
    {
        var midia = resultado.GetValue(argMidia)!;
        var wav = Path.Combine(Path.GetTempPath(), $"transcritor-simular-{Guid.NewGuid():N}.wav");
        try
        {
            await sp.GetRequiredService<Transcritor.Arquivo.Midia.IConversorMidia>().ConverterParaWavAsync(midia.FullName, wav, null, ct);
            await using var fonte = new FonteAudioWav(wav, resultado.GetValue(optVelocidade));
            var transcritor = sp.GetRequiredService<ITranscritorTempoReal>();
            await MostrarEventos(transcritor.IniciarAsync(fonte, resultado.GetValue(optSaidaTr), Sessao(resultado), ct));
            return 0;
        }
        finally
        {
            File.Delete(wav);
        }
    });
});

var optFonte = new Option<Fonte>("--fonte") { Description = "Microfone ou SomDoSistema.", DefaultValueFactory = _ => Fonte.Microfone };
var optSegundos = new Option<int>("--segundos") { Description = "Duração da gravação.", DefaultValueFactory = _ => 5 };
var optWav = new Option<FileInfo?>("--wav") { Description = "Salva o áudio capturado (16 kHz mono) neste WAV." };
var cmdCaptura = new Command("captura", "Grava alguns segundos da fonte e mostra o nível por segundo (testa a captura sem o WhisperLiveKit).")
{
    optFonte, optSegundos, optWav,
};
cmdCaptura.SetAction(async (resultado, ct) => await Executar(async () =>
{
    var fonteEscolhida = resultado.GetValue(optFonte);
    var segundos = resultado.GetValue(optSegundos);
    await using var fonte = new FonteAudioWasapi(fonteEscolhida);
    Console.WriteLine($"{fonteEscolhida}: {fonte.NomeDispositivo()} — gravando {segundos} s...");

    using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
    limite.CancelAfter(TimeSpan.FromSeconds(segundos));
    using var pcm = new MemoryStream();
    var segundoAtual = 0;
    var pico = 0;
    await foreach (var bloco in fonte.CapturarAsync(limite.Token))
    {
        pcm.Write(bloco.Span);
        for (var i = 0; i + 1 < bloco.Length; i += 2)
        {
            pico = Math.Max(pico, Math.Abs((int)BitConverter.ToInt16(bloco.Span.Slice(i, 2))));
        }

        if (pcm.Length / 32000 > segundoAtual)
        {
            segundoAtual = (int)(pcm.Length / 32000);
            var pct = pico * 100 / short.MaxValue;
            Console.WriteLine($"  {segundoAtual,2}s  pico {pct,3}%  {new string('#', pct / 2)}");
            pico = 0;
        }
    }

    Console.WriteLine($"Capturado: {pcm.Length / 32000d:F1} s de áudio 16 kHz mono ({pcm.Length} bytes).");
    var wav = resultado.GetValue(optWav);
    if (wav is not null)
    {
        await File.WriteAllBytesAsync(wav.FullName, CabecalhoWav((int)pcm.Length).Concat(pcm.ToArray()).ToArray(), ct);
        Console.WriteLine($"WAV: {wav.FullName}");
    }

    return 0;
}));

var raiz = new RootCommand("Cli de teste da Transcritor lib.")
{
    cmdArquivo, cmdTraduzir, cmdPalavra, cmdMicrofone, cmdSistema, cmdSimular, cmdCaptura, cmdServicos,
};
return await raiz.Parse(args).InvokeAsync();

static byte[] CabecalhoWav(int bytesDados)
{
    using var m = new MemoryStream();
    using var w = new BinaryWriter(m);
    w.Write("RIFF"u8); w.Write(36 + bytesDados); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(bytesDados);
    w.Flush();
    return m.ToArray();
}

// Terminal: o provisório fica numa linha que se reescreve. Saída redirecionada: uma linha por evento.
static async Task MostrarEventos(IAsyncEnumerable<EventoTempoReal> eventos)
{
    var simples = Console.IsOutputRedirected;
    var relogio = Stopwatch.StartNew();
    var provisorios = 0;
    var confirmados = 0;
    await foreach (var e in eventos)
    {
        var quando = $"{relogio.Elapsed.TotalSeconds,6:F1}s";
        if (e.Tipo == TipoEvento.Provisorio)
        {
            provisorios++;
            if (simples)
            {
                Console.WriteLine($"{quando} PROV  #{e.Id} [{e.Idioma}] {e.Texto}");
            }
            else
            {
                // Uma linha só: se o texto quebrasse, "\r" voltaria só para a última linha visual e o
                // redesenho apagaria/embaralharia os confirmados de cima. Mostra o final do texto.
                var largura = Math.Max(20, LarguraConsole() - 3);
                var texto = e.Texto.Length <= largura ? e.Texto : "…" + e.Texto[^(largura - 1)..];
                Console.Write($"\r\u001b[2K\u001b[90m… {texto}\u001b[0m");
            }

            continue;
        }

        if (e.Traducao is null)
        {
            confirmados++;
            var linha = $"CONF  #{e.Id} [{e.Idioma}] ({e.Inicio:F1}–{e.Fim:F1}s) {e.Texto}";
            Console.WriteLine(simples ? $"{quando} {linha}" : $"\r\u001b[2K{linha}");
        }
        else
        {
            var linha = $"TRAD  #{e.Id} → {e.Traducao}";
            Console.WriteLine(simples ? $"{quando} {linha}" : $"\r\u001b[2K\u001b[36m{linha}\u001b[0m");
        }
    }

    Console.WriteLine(simples ? $"Fim: {provisorios} provisórios, {confirmados} confirmados." : $"\r\u001b[2KFim: {provisorios} provisórios, {confirmados} confirmados.");
}

static int LarguraConsole()
{
    try
    {
        return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
    }
    catch (IOException)
    {
        return 80;
    }
}

// ---- apoio -----------------------------------------------------------------
static ServiceProvider CriarServicos(Action<OpcoesArquivo>? ajustar, Action<OpcoesTraducao>? ajustarTraducao = null)
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables("TRANSCRITOR_")
        .Build();

    var services = new ServiceCollection();
    services.AddTranscritorArquivo();
    services.AddTranscritorTraducao();
    services.AddTranscritorTempoReal();
    services.AddTranscritorCaptura();
    services.Configure<OpcoesArquivo>(config.GetSection("Transcritor:Arquivo"));
    services.Configure<OpcoesTraducao>(config.GetSection("Transcritor:Traducao"));
    services.Configure<OpcoesTempoReal>(config.GetSection("Transcritor:TempoReal"));
    if (ajustar is not null)
    {
        services.PostConfigure(ajustar);
    }

    if (ajustarTraducao is not null)
    {
        services.PostConfigure(ajustarTraducao);
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
