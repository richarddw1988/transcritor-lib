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
using Transcritor.Dicionario;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

// ---- arquivo ---------------------------------------------------------------
var argCaminhos = new Argument<FileInfo[]>("caminhos")
{
    Description = "Um ou mais áudios ou vídeos (mp4, mp3, mkv, wav, ...). Vários: processa em lote, uma etapa por vez para todos.",
    Arity = ArgumentArity.OneOrMore,
};
var optModelo = new Option<string?>("--modelo") { Description = "Modelo do Speaches (padrão: appsettings.json)." };
var optIdioma = new Option<string?>("--idioma") { Description = "Força o idioma falado: pt ou en (padrão: detecção automática)." };
var optJson = new Option<FileInfo?>("--json") { Description = "Também grava o JSON neste caminho." };
var optSaida = new Option<Saida>("--saida")
{
    Description = "Portugues, Ingles ou Ambos. O texto fica no idioma falado; a tradução vem quando pedida.",
    DefaultValueFactory = _ => Saida.Ambos,
};

var optSentido = new Option<bool>("--sentido") { Description = "Também gera blocos (expressões) e o que quis dizer em cada frase (lento: ~13 s/frase com gemma3:12b)." };

var optCorrigirArquivo = new Option<bool>("--corrigir")
{
    Description = "Experimental: corrige palavras ouvidas errado antes de traduzir (troca por outra de som parecido; ~3,5 s por frase duvidosa).",
};

var cmdArquivo = new Command("arquivo", "Transcreve (e traduz) um ou mais arquivos e grava o JSON de cada um com frases e palavras.")
{
    argCaminhos, optSaida, optModelo, optIdioma, optJson, optSentido, optCorrigirArquivo,
};
cmdArquivo.SetAction(async (resultado, ct) =>
{
    var caminhos = resultado.GetValue(argCaminhos)!;
    var saida = resultado.GetValue(optSaida);
    var modelo = resultado.GetValue(optModelo);
    var idioma = resultado.GetValue(optIdioma);
    var json = resultado.GetValue(optJson);
    var sentido = resultado.GetValue(optSentido);
    var corrigirArquivo = resultado.GetValue(optCorrigirArquivo);

    using var sp = CriarServicos(
        o =>
        {
            if (!string.IsNullOrWhiteSpace(modelo)) o.ModeloTranscricao = modelo;
            if (!string.IsNullOrWhiteSpace(idioma)) o.Idioma = idioma;
        },
        t =>
        {
            if (sentido) t.AnalisarSentido = true;
            if (corrigirArquivo) t.CorrigirTranscricao = true;
        });

    return await Executar(async () =>
    {
        if (json is not null && caminhos.Length > 1)
        {
            throw new ArgumentException("--json só vale para um arquivo; em lote, o JSON de cada um fica ao lado da mídia.");
        }

        var transcritor = sp.GetRequiredService<ITranscritorArquivo>();
        var relogio = Stopwatch.StartNew();
        var progresso = new ProgressoConsole();
        var resultados = await transcritor.ProcessarLoteAsync(caminhos.Select(c => c.FullName).ToList(), saida, progresso, ct);
        relogio.Stop();

        if (caminhos.Length > 1)
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Lote:     {caminhos.Length} arquivos em {relogio.Elapsed.TotalSeconds:F1} s");
            foreach (var (c, lido) in caminhos.Zip(resultados))
            {
                Console.WriteLine($"  {c.Name}: {lido.Arquivo.Idioma}, {TimeSpan.FromSeconds(lido.Arquivo.Duracao):hh\\:mm\\:ss}, {lido.Frases.Count} frases → {OpcoesArquivo.CaminhoJsonAoLado(c.FullName)}");
            }

            return 0;
        }

        var caminho = caminhos[0];
        var r = resultados[0];
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

// ---- liberar-gpu -----------------------------------------------------------
var optManterTempoReal = new Option<bool>("--manter-tempo-real") { Description = "Não para o WhisperLiveKit (só descarrega Ollama e Speaches)." };
var optConteinerWlk = new Option<string>("--conteiner")
{
    Description = "Nome do contêiner do WhisperLiveKit.",
    DefaultValueFactory = _ => "whisperlivekit",
};
var cmdLiberarGpu = new Command(
    "liberar-gpu",
    "Tira da GPU os modelos do Ollama e do Speaches e para o WhisperLiveKit (ele carrega o modelo ao subir e só solta parando). Encerra sessões de tempo real abertas.")
{
    optManterTempoReal, optConteinerWlk,
};
cmdLiberarGpu.SetAction(async (resultado, ct) => await Executar(async () =>
{
    using var sp = CriarServicos(null);
    var executor = sp.GetRequiredService<Transcritor.Arquivo.Processos.IExecutorProcesso>();
    var antes = await MemoriaGpuAsync(executor, ct);

    // Ollama: todos os modelos carregados, não só os da configuração.
    var urlOllama = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcoesTraducao>>().Value.OllamaUrl;
    using (var http = new HttpClient { BaseAddress = new Uri(urlOllama, "/"), Timeout = TimeSpan.FromSeconds(30) })
    {
        try
        {
            using var ps = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("api/ps", ct));
            var modelos = ps.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();
            foreach (var modelo in modelos)
            {
                using var corpo = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { model = modelo, keep_alive = 0 }), Encoding.UTF8, "application/json");
                (await http.PostAsync("api/generate", corpo, ct)).Dispose();
            }

            Console.WriteLine($"Ollama:         {(modelos.Count == 0 ? "nada carregado" : "descarregado " + string.Join(", ", modelos))}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"Ollama:         não respondeu em {urlOllama} ({ex.Message})");
        }
    }

    // Speaches: pelo motor da lib, que só manda o DELETE se a imagem tiver a correção (senão espera o tempo ocioso).
    if (sp.GetRequiredService<IMotorTranscricao>() is IDescarregavel motor)
    {
        var relogio = Stopwatch.StartNew();
        await motor.DescarregarAsync(ct);
        Console.WriteLine($"Speaches:       Whisper descarregado ({relogio.Elapsed.TotalSeconds:F1} s)");
    }

    if (!resultado.GetValue(optManterTempoReal))
    {
        var conteiner = resultado.GetValue(optConteinerWlk)!;
        var relogio = Stopwatch.StartNew();
        Transcritor.Arquivo.Processos.ResultadoProcesso r;
        try
        {
            r = await executor.ExecutarAsync("docker", ["stop", conteiner], null, ct);
        }
        catch (FerramentaExternaException)
        {
            r = new Transcritor.Arquivo.Processos.ResultadoProcesso(-1, string.Empty, "docker não encontrado no PATH");
        }

        Console.WriteLine(r.CodigoSaida == 0
            ? $"WhisperLiveKit: parado ({relogio.Elapsed.TotalSeconds:F1} s); para o tempo real voltar: docker start {conteiner}"
            : $"WhisperLiveKit: não parou ({r.Erro.Trim()})");
    }

    await Task.Delay(TimeSpan.FromSeconds(2), ct);  // o driver demora um pouco para mostrar a memória devolvida
    var depois = await MemoriaGpuAsync(executor, ct);
    if (antes is not null && depois is not null)
    {
        Console.WriteLine($"GPU:            {antes / 1024.0:F1} GB → {depois / 1024.0:F1} GB em uso (contando outros programas)");
    }

    return 0;
}));

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
var optSegundos = new Option<int>("--segundos") { Description = "Duração da gravação (com --ate-enter, o máximo).", DefaultValueFactory = _ => 5 };
var optAteEnter = new Option<bool>("--ate-enter") { Description = "Grava até apertar Enter (no máximo --segundos)." };
var optWav = new Option<FileInfo?>("--wav") { Description = "Salva o áudio capturado (16 kHz mono) neste WAV." };
var cmdCaptura = new Command("captura", "Grava alguns segundos da fonte e mostra o nível por segundo (testa a captura sem o WhisperLiveKit).")
{
    optFonte, optSegundos, optWav, optAteEnter,
};
cmdCaptura.SetAction(async (resultado, ct) => await Executar(async () =>
{
    var fonteEscolhida = resultado.GetValue(optFonte);
    var segundos = resultado.GetValue(optSegundos);
    var ateEnter = resultado.GetValue(optAteEnter);
    await using var fonte = new FonteAudioWasapi(fonteEscolhida);
    Console.WriteLine($"{fonteEscolhida}: {fonte.NomeDispositivo()} — gravando {(ateEnter ? $"até o Enter (máximo {segundos} s)" : $"{segundos} s")}...");

    using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (ateEnter)
    {
        // Enter antes do aviso "pode falar" também para: quem aperta cedo demais grava de novo.
        _ = Task.Run(() =>
        {
            Console.ReadLine();
            limite.Cancel();
        });
    }

    using var pcm = new MemoryStream();
    var segundoAtual = 0;
    var pico = 0;
    await foreach (var bloco in fonte.CapturarAsync(limite.Token))
    {
        if (pcm.Length == 0)
        {
            // O dispositivo leva um tempo para abrir: o prazo só conta a partir do primeiro áudio.
            limite.CancelAfter(TimeSpan.FromSeconds(segundos));
            Console.WriteLine(ateEnter ? "  >>> Gravando: pode falar (Enter quando terminar)." : "  >>> Gravando: pode falar.");
        }

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

// ---- avaliar-palavras ------------------------------------------------------
var argCasos = new Argument<FileInfo>("casos") { Description = "JSON com os casos (ex.: docs/avaliacao/palavras-ambiguas.json)." };
var optSemDicionario = new Option<bool>("--sem-dicionario") { Description = "Traduz só com o modelo, sem as opções do dicionário." };
var optSemTraducaoFrase = new Option<bool>("--sem-traducao-frase") { Description = "Não passa a tradução da frase (a lib traduz a frase antes)." };
var cmdAvaliarPalavras = new Command("avaliar-palavras", "Mede acerto e tempo da tradução de palavra num conjunto de casos (sem cache).")
{
    argCasos, optSemDicionario, optSemTraducaoFrase,
};
cmdAvaliarPalavras.SetAction(async (resultado, ct) =>
{
    var cacheTemporario = Path.Combine(Path.GetTempPath(), $"transcritor-avaliacao-{Guid.NewGuid():N}.json");
    var semDicionario = resultado.GetValue(optSemDicionario);
    using var sp = CriarServicos(null, o =>
    {
        o.CaminhoCachePalavras = cacheTemporario;
        if (semDicionario)
        {
            o.CaminhoDicionario = null;
        }
    });
    return await Executar(async () =>
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultado.GetValue(argCasos)!.FullName, ct));
        var tradutor = sp.GetRequiredService<ITradutor>();
        var semTraducaoFrase = resultado.GetValue(optSemTraducaoFrase);
        var acertos = 0;
        var total = 0;
        var tempos = new List<double>();
        foreach (var caso in doc.RootElement.EnumerateArray())
        {
            var pedido = new PedidoTraducaoPalavra(
                caso.GetProperty("palavra").GetString()!,
                caso.GetProperty("frase").GetString()!,
                caso.GetProperty("de").GetString()!,
                caso.GetProperty("para").GetString()!,
                semTraducaoFrase ? null : caso.GetProperty("traducao").GetString());
            var aceitas = caso.GetProperty("aceitas").EnumerateArray().Select(a => a.GetString()!.ToLowerInvariant()).ToList();
            var relogio = Stopwatch.StartNew();
            var traducao = await tradutor.TraduzirPalavraAsync(pedido, ct);
            tempos.Add(relogio.Elapsed.TotalSeconds);
            var certo = aceitas.Any(a => traducao.ToLowerInvariant().Contains(a, StringComparison.Ordinal));
            acertos += certo ? 1 : 0;
            total++;
            Console.WriteLine($"{(certo ? "[ OK ]" : "[ERRO]")} {pedido.Palavra,-10} {traducao,-28} ({relogio.Elapsed.TotalSeconds:F1} s)  {pedido.Frase}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Acertos: {acertos}/{total} ({100.0 * acertos / Math.Max(1, total):F0}%); tempo médio {tempos.DefaultIfEmpty().Average():F2} s " +
            $"(1º caso {tempos.FirstOrDefault():F1} s, inclui carregar o modelo); dicionário: {(semDicionario ? "não" : "sim")}; " +
            $"tradução da frase: {(semTraducaoFrase ? "não" : "sim")}");
        File.Delete(cacheTemporario);
        return 0;
    });
});

// ---- dicionario ------------------------------------------------------------
var optBanco = new Option<FileInfo>("--banco")
{
    Description = "SQLite do dicionário.",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("dados", "dicionario.db")),
};
var optIngles = new Option<FileInfo>("--ingles")
{
    Description = "Dump do Wiktionary com os verbetes em inglês (kaikki.org, .jsonl ou .jsonl.gz).",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("dados", "kaikki-en.jsonl.gz")),
};
var optPortugues = new Option<FileInfo>("--portugues")
{
    Description = "Dump do Wiktionary com os verbetes em português (kaikki.org, .jsonl ou .jsonl.gz).",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("dados", "kaikki-pt.jsonl.gz")),
};
var optFreqIngles = new Option<FileInfo>("--frequencia-ingles")
{
    Description = "Lista de frequência do inglês (FrequencyWords en_50k.txt). Sem as duas listas, a dica de palavras raras fica desligada.",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("dados", "en_50k.txt")),
};
var optFreqPortugues = new Option<FileInfo>("--frequencia-portugues")
{
    Description = "Lista de frequência do português (FrequencyWords pt_br_50k.txt).",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("dados", "pt_br_50k.txt")),
};
var cmdImportar = new Command("importar", "Gera o SQLite do dicionário inglês ↔ português a partir dos dumps do Wiktionary.")
{
    optIngles, optPortugues, optFreqIngles, optFreqPortugues, optBanco,
};
cmdImportar.SetAction(async (resultado, ct) => await Executar(async () =>
{
    var progresso = new Progress<string>(m => Console.WriteLine($"  {m}"));
    var freqIngles = resultado.GetValue(optFreqIngles)!;
    var freqPortugues = resultado.GetValue(optFreqPortugues)!;
    ListasFrequencia? frequencia = null;
    if (freqIngles.Exists && freqPortugues.Exists)
    {
        frequencia = new ListasFrequencia(freqIngles.FullName, freqPortugues.FullName);
    }
    else
    {
        Console.WriteLine($"Aviso: sem as listas de frequência ({freqIngles.FullName}, {freqPortugues.FullName}); " +
            "a dica de palavras raras na tradução das frases fica desligada (ver docs/manual.md, seção 4.6).");
    }

    var resumo = await ImportadorWiktionary.ImportarAsync(
        resultado.GetValue(optIngles)!.FullName,
        resultado.GetValue(optPortugues)!.FullName,
        resultado.GetValue(optBanco)!.FullName,
        frequencia,
        progresso,
        ct);
    Console.WriteLine(
        $"Pronto em {resumo.Duracao.TotalSeconds:F0} s: {resumo.Traducoes:N0} traduções, {resumo.Definicoes:N0} definições, " +
        $"{resumo.Formas:N0} flexões, {resumo.ChavesFoneticas:N0} palavras com chave de pronúncia, " +
        $"{resumo.PalavrasFrequencia:N0} palavras com frequência, {resumo.Homofonos:N0} pares de homófonos; " +
        $"{resumo.TamanhoBytes / 1024d / 1024d:F0} MB em {resultado.GetValue(optBanco)!.FullName}");
    return 0;
}));

var argTermo = new Argument<string>("termo") { Description = "Palavra ou expressão (ex.: gave, \"give up\", desisti)." };
var cmdProcurar = new Command("procurar", "Mostra os sentidos de um termo no dicionário local.")
{
    argTermo, optDe, optPara, optBanco,
};
cmdProcurar.SetAction(async (resultado, ct) => await Executar(() =>
{
    using var dicionario = DicionarioSqlite.Abrir(resultado.GetValue(optBanco)!.FullName);
    var relogio = Stopwatch.StartNew();
    var verbetes = dicionario.Procurar(resultado.GetValue(argTermo)!, resultado.GetValue(optDe)!, resultado.GetValue(optPara)!);
    var tempo = relogio.Elapsed;
    foreach (var v in verbetes)
    {
        var lema = v.Lema is null ? string.Empty : $" (de {v.Lema})";
        var sentido = v.Sentido is null ? string.Empty : $"  [{v.Sentido}]";
        var tipo = v.Tipo == TipoVerbete.Definicao ? " (definição)" : string.Empty;
        Console.WriteLine($"{v.Termo}{lema} · {v.Classe}: {v.Traducao}{tipo}{sentido}");
    }

    Console.Error.WriteLine($"({verbetes.Count} verbetes, {tempo.TotalMilliseconds:F1} ms)");
    return Task.FromResult(verbetes.Count > 0 ? 0 : 3);
}));

var argParecida = new Argument<string>("palavra") { Description = "Palavra como foi reconhecida (ex.: arts)." };
var optIdiomaDic = new Option<string>("--idioma") { Description = "Idioma da palavra: pt ou en.", Required = true };
var optMaximo = new Option<int>("--maximo") { Description = "Quantas palavras mostrar.", DefaultValueFactory = _ => 10 };
var cmdParecidas = new Command("parecidas", "Palavras do dicionário que soam parecido (para corrigir erro de reconhecimento de fala).")
{
    argParecida, optIdiomaDic, optMaximo, optBanco,
};
cmdParecidas.SetAction(async (resultado, ct) => await Executar(() =>
{
    using var dicionario = DicionarioSqlite.Abrir(resultado.GetValue(optBanco)!.FullName);
    var relogio = Stopwatch.StartNew();
    var parecidas = dicionario.ProcurarParecidas(resultado.GetValue(argParecida)!, resultado.GetValue(optIdiomaDic)!, resultado.GetValue(optMaximo));
    var tempo = relogio.Elapsed;
    foreach (var p in parecidas)
    {
        Console.WriteLine($"{p.Termo,-20} pronúncia {p.DistanciaPronuncia}  escrita {p.DistanciaEscrita}  verbetes {p.Sentidos}");
    }

    Console.Error.WriteLine($"({parecidas.Count} palavras, {tempo.TotalMilliseconds:F1} ms, inclui carregar as chaves na 1ª consulta)");
    return Task.FromResult(parecidas.Count > 0 ? 0 : 3);
}));

var cmdDicionario = new Command("dicionario", "Dicionário inglês ↔ português local (Wiktionary em SQLite).")
{
    cmdImportar, cmdProcurar, cmdParecidas,
};

// ---- avaliar-fala ----------------------------------------------------------
var optFrasesFala = new Option<FileInfo>("--frases")
{
    Description = "JSON com as frases gravadas (id, idioma, texto).",
    DefaultValueFactory = _ => new FileInfo(Path.Combine("docs", "avaliacao", "frases-para-gravar.json")),
};
var optPastaFala = new Option<DirectoryInfo>("--pasta")
{
    Description = "Pasta com os WAV (<id>.wav), gravados com scripts/gravar-frases.ps1.",
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine("dados", "gravacoes")),
};
var optLimite = new Option<double>("--limite-confianca")
{
    Description = "Palavra com confiança abaixo disto conta como suspeita.",
    DefaultValueFactory = _ => 0.5,
};
var optCorrigir = new Option<bool>("--corrigir")
{
    Description = "Passa cada frase pela correção de palavras ouvidas errado (modelo de ModeloCorrecao) e mede antes e depois.",
};
var cmdAvaliarFala = new Command("avaliar-fala", "Transcreve as gravações, compara com o texto certo e mede erros, confiança e palavras parecidas.")
{
    optFrasesFala, optPastaFala, optLimite, optBanco, optCorrigir,
};
cmdAvaliarFala.SetAction(async (resultado, ct) => await Executar(async () =>
{
    using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultado.GetValue(optFrasesFala)!.FullName, ct));
    var pasta = resultado.GetValue(optPastaFala)!;
    var limite = resultado.GetValue(optLimite);
    var banco = resultado.GetValue(optBanco)!;
    using var dicionario = banco.Exists ? DicionarioSqlite.Abrir(banco.FullName) : null;
    var corrigir = resultado.GetValue(optCorrigir);
    int palavras = 0, trocas = 0, faltas = 0, sobras = 0, erradasSuspeitas = 0, certasSuspeitas = 0, trocasNaLista = 0, frases = 0;
    int errosAntes = 0, corrigidasCertas = 0, corrigidasErradas = 0;
    var tempoCorrecao = TimeSpan.Zero;
    foreach (var item in doc.RootElement.EnumerateArray())
    {
        var id = item.GetProperty("id").GetString()!;
        var idioma = item.GetProperty("idioma").GetString()!;
        var texto = item.GetProperty("texto").GetString()!;
        var wav = Path.Combine(pasta.FullName, id + ".wav");
        if (!File.Exists(wav))
        {
            continue;
        }

        // A correção usa o mesmo dicionário das parecidas mostradas (--banco), não o do appsettings.json.
        using var sp = CriarServicos(
            o =>
            {
                o.Idioma = idioma;
                o.GravarJsonAoLado = false;
            },
            t => t.CaminhoDicionario = banco.Exists ? banco.FullName : null);
        var r = await sp.GetRequiredService<ITranscritorArquivo>().ProcessarAsync(
            wav, idioma == Idiomas.Ingles ? Saida.Ingles : Saida.Portugues, null, ct);
        var reconhecidas = r.Frases.SelectMany(f => f.Palavras).ToList();
        Console.WriteLine($"{id}: {texto}");
        Console.WriteLine($"     {string.Join(' ', reconhecidas.Select(p => p.Texto))}");
        if (corrigir)
        {
            // Correção pelo pipeline do arquivo (texto + conferência no áudio, conforme a configuração).
            errosAntes += AlinharPalavras(PalavrasNormalizadas(texto), reconhecidas).Count(a => a.Certa != a.Ouvida);
            using var spCorrecao = CriarServicos(
                o =>
                {
                    o.Idioma = idioma;
                    o.GravarJsonAoLado = false;
                },
                t => t.CorrigirTranscricao = true);
            var relogioCorrecao = Stopwatch.StartNew();
            var corrigido = await spCorrecao.GetRequiredService<ITranscritorArquivo>().ProcessarAsync(
                wav, idioma == Idiomas.Ingles ? Saida.Ingles : Saida.Portugues, null, ct);
            tempoCorrecao += relogioCorrecao.Elapsed;
            var certasDaFrase = PalavrasNormalizadas(texto);
            int Erros(IReadOnlyList<Palavra> lista) => AlinharPalavras(certasDaFrase, lista).Count(a => a.Certa != a.Ouvida);
            var errosSemCorrecao = Erros(reconhecidas);
            var original = reconhecidas.ToList();
            var novas = corrigido.Frases.SelectMany(f => f.Palavras).ToList();
            var trocadas = Enumerable.Range(0, Math.Min(novas.Count, original.Count)).Where(i => novas[i].Ouvida is not null).ToList();
            foreach (var i in trocadas)
            {
                // Certa = sozinha, esta troca diminui os erros da frase.
                var soEsta = original.ToList();
                soEsta[i] = soEsta[i] with { Texto = novas[i].Texto };
                var acertou = Erros(soEsta) < errosSemCorrecao;
                corrigidasCertas += acertou ? 1 : 0;
                corrigidasErradas += acertou ? 0 : 1;
                Console.WriteLine($"     corrigida: \"{novas[i].Ouvida}\" → \"{novas[i].Texto}\"{(acertou ? "" : " (ERRADA)")}");
            }

            if (trocadas.Count > 0)
            {
                reconhecidas = novas;
                var melhora = errosSemCorrecao - Erros(reconhecidas);
                Console.WriteLine($"     {string.Join(' ', reconhecidas.Select(p => p.Texto))}  ({(melhora >= 0 ? "-" : "+")}{Math.Abs(melhora)} erros)");
            }
        }

        var alinhado = AlinharPalavras(PalavrasNormalizadas(texto), reconhecidas);
        frases++;
        palavras += alinhado.Count(a => a.Certa is not null);
        foreach (var (certa, ouvida, confianca) in alinhado)
        {
            var suspeita = confianca is { } c && c < limite;
            if (certa == ouvida)
            {
                certasSuspeitas += suspeita ? 1 : 0;
                continue;
            }

            erradasSuspeitas += ouvida is not null && suspeita ? 1 : 0;
            var marca = suspeita ? "suspeita" : "confiante";
            if (certa is not null && ouvida is not null)
            {
                trocas++;
                var posicao = dicionario?.ProcurarParecidas(ouvida, idioma, 15).Select(p => p.Termo).ToList().IndexOf(certa) ?? -2;
                trocasNaLista += posicao >= 0 ? 1 : 0;
                var lista = posicao switch { -2 => "", -1 => "; certa fora das 15 parecidas", _ => $"; certa é a {posicao + 1}ª parecida" };
                Console.WriteLine($"     troca: \"{certa}\" → \"{ouvida}\" (confiança {confianca:F2}, {marca}{lista})");
            }
            else if (certa is not null)
            {
                faltas++;
                Console.WriteLine($"     faltou: \"{certa}\"");
            }
            else
            {
                sobras++;
                Console.WriteLine($"     sobrou: \"{ouvida}\" (confiança {confianca:F2}, {marca})");
            }
        }
    }

    if (frases == 0)
    {
        Console.Error.WriteLine($"Nenhuma gravação em {pasta.FullName}. Grave com: .\\scripts\\gravar-frases.ps1");
        return 3;
    }

    var erros = trocas + faltas + sobras;
    Console.WriteLine();
    Console.WriteLine($"{frases} frases, {palavras} palavras: WER {100.0 * erros / Math.Max(1, palavras):F1}% ({trocas} trocas, {faltas} faltas, {sobras} sobras)");
    Console.WriteLine(
        $"Confiança < {limite:F2}: {erradasSuspeitas} de {trocas + sobras} palavras erradas e {certasSuspeitas} certas " +
        $"(alarmes falsos); certa entre as 15 parecidas em {trocasNaLista} de {trocas} trocas" +
        (dicionario is null ? " (sem dicionário)" : string.Empty));
    if (corrigir)
    {
        Console.WriteLine(
            $"Correção: WER {100.0 * errosAntes / Math.Max(1, palavras):F1}% antes → {100.0 * erros / Math.Max(1, palavras):F1}% depois; " +
            $"{corrigidasCertas} trocas certas e {corrigidasErradas} erradas; {tempoCorrecao.TotalSeconds / Math.Max(1, frases):F1} s por frase");
    }

    return 0;
}));

var raiz = new RootCommand("Cli de teste da Transcritor lib.")
{
    cmdArquivo, cmdTraduzir, cmdPalavra, cmdMicrofone, cmdSistema, cmdSimular, cmdCaptura, cmdServicos, cmdLiberarGpu, cmdDicionario, cmdAvaliarPalavras, cmdAvaliarFala,
};
return await raiz.Parse(args).InvokeAsync();

/// <summary>Minúsculas, sem pontuação em volta; hífen separa ("steel-rimmed" = "steel" "rimmed").</summary>
static List<string> PalavrasNormalizadas(string texto) =>
    texto.ToLowerInvariant().Replace('’', '\'')
        .Split([' ', '-', '—', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Trim('.', ',', '!', '?', ';', ':', '"', '“', '”', '(', ')', '…', '\''))
        .Where(p => p.Length > 0)
        .ToList();

/// <summary>
/// Alinha o texto certo com as palavras reconhecidas (distância de edição por palavra) e devolve cada par:
/// (certa, ouvida, confiança da ouvida). Falta: ouvida nula; sobra: certa nula.
/// </summary>
static List<(string? Certa, string? Ouvida, double? Confianca)> AlinharPalavras(List<string> certas, IReadOnlyList<Palavra> reconhecidas)
{
    var ouvidas = reconhecidas
        .SelectMany(p => PalavrasNormalizadas(p.Texto).Select(t => (Texto: t, p.Confianca)))
        .ToList();
    var custo = new int[certas.Count + 1, ouvidas.Count + 1];
    for (var i = 0; i <= certas.Count; i++) custo[i, 0] = i;
    for (var j = 0; j <= ouvidas.Count; j++) custo[0, j] = j;
    for (var i = 1; i <= certas.Count; i++)
    {
        for (var j = 1; j <= ouvidas.Count; j++)
        {
            var troca = custo[i - 1, j - 1] + (certas[i - 1] == ouvidas[j - 1].Texto ? 0 : 1);
            custo[i, j] = Math.Min(troca, Math.Min(custo[i - 1, j] + 1, custo[i, j - 1] + 1));
        }
    }

    var pares = new List<(string?, string?, double?)>();
    int a = certas.Count, b = ouvidas.Count;
    while (a > 0 || b > 0)
    {
        if (a > 0 && b > 0 && custo[a, b] == custo[a - 1, b - 1] + (certas[a - 1] == ouvidas[b - 1].Texto ? 0 : 1))
        {
            pares.Add((certas[a - 1], ouvidas[b - 1].Texto, ouvidas[b - 1].Confianca));
            a--;
            b--;
        }
        else if (a > 0 && custo[a, b] == custo[a - 1, b] + 1)
        {
            pares.Add((certas[a - 1], null, null));
            a--;
        }
        else
        {
            pares.Add((null, ouvidas[b - 1].Texto, ouvidas[b - 1].Confianca));
            b--;
        }
    }

    pares.Reverse();
    return pares;
}

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

/// <summary>Memória usada na GPU em MiB (nvidia-smi), ou null se não der para ler.</summary>
static async Task<int?> MemoriaGpuAsync(Transcritor.Arquivo.Processos.IExecutorProcesso executor, CancellationToken ct)
{
    try
    {
        var r = await executor.ExecutarAsync("nvidia-smi", ["--query-gpu=memory.used", "--format=csv,noheader,nounits"], null, ct);
        return r.CodigoSaida == 0 && int.TryParse(r.Saida.Split('\n')[0].Trim(), out var mib) ? mib : null;
    }
    catch (FerramentaExternaException)
    {
        return null;
    }
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
    private int _arquivo = 1;

    public bool DoCache { get; private set; }

    public void Report(ProgressoProcessamento value)
    {
        DoCache |= value.DoCache;
        var decimo = value.Percentual is { } p ? (int)(p / 10) : -1;
        if (value.Etapa == _etapa && decimo == _ultimoDecimo && value.Arquivo == _arquivo)
        {
            return;
        }

        if ((value.Etapa != _etapa || value.Arquivo != _arquivo) && _etapa is not null)
        {
            Console.WriteLine();
        }

        _arquivo = value.Arquivo;

        _etapa = value.Etapa;
        _ultimoDecimo = decimo;
        var percentual = value.Percentual is { } q ? $" {q,5:F1}%" : string.Empty;
        var arquivo = value.TotalArquivos > 1 ? $"[{value.Arquivo}/{value.TotalArquivos}] " : string.Empty;
        Console.Write($"\r{arquivo}{value.Etapa,-18}{percentual}");
    }
}
