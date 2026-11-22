using Microsoft.Extensions.DependencyInjection;
using Transcritor.Arquivo;
using Transcritor.Captura;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;
using Transcritor.Dicionario;
using Transcritor.TempoReal;
using Transcritor.Traducao;

namespace Transcritor.Tests;

/// <summary>
/// Os exemplos da seção 5 de docs/manual.md. Se a API mudar e este arquivo não compilar,
/// atualize o manual junto. O único teste confere que o registro no DI resolve tudo.
/// </summary>
public class ExemplosDoManualTests
{
    [Fact]
    public void Registro_do_manual_resolve_todos_os_servicos()
    {
        var services = new ServiceCollection();
        services.AddTranscritorArquivo();
        services.AddTranscritorTraducao();
        services.AddTranscritorTempoReal();
        services.AddTranscritorCaptura();
        services.AddTranscritorTraducao(o => o.OllamaUrl = new Uri("http://outra-maquina:11434"));
        services.AddTranscritorTraducao(o =>
            o.Glossario.Add(new EntradaGlossario { Origem = "en", Termo = "Giant Peach", Traducao = "Pêssego Gigante" }));
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<ITranscritorArquivo>());
        Assert.NotNull(sp.GetRequiredService<ITradutor>());
        Assert.NotNull(sp.GetRequiredService<ITranscritorTempoReal>());
        Assert.NotNull(sp.GetRequiredService<IAnalisadorSentido>());
        Assert.NotNull(sp.GetRequiredService<ICorretorTranscricao>());
        Assert.NotNull(sp.GetRequiredService<IFabricaFonteAudio>());
    }

    // Só precisa compilar (seção 5.1).
    internal static async Task Arquivo(IServiceProvider sp, CancellationToken ct)
    {
        var transcritor = sp.GetRequiredService<ITranscritorArquivo>();
        var progresso = new Progress<ProgressoProcessamento>(p => Console.WriteLine($"{p.Etapa} {p.Percentual:F0}%"));
        ResultadoTranscricao r = await transcritor.ProcessarAsync(@"C:\videos\aula.mp4", Saida.Ambos, progresso, ct);
        foreach (var frase in r.Frases)
        {
            Console.WriteLine($"[{frase.Inicio:F1}] {frase.Texto} → {frase.Traducao}");
        }

        _ = await JsonTranscricao.LerAsync(@"C:\videos\aula.mp4.transcricao.json", ct);
    }

    // Seção 5.2.
    internal static async Task Traducao(IServiceProvider sp, CancellationToken ct)
    {
        var tradutor = sp.GetRequiredService<ITradutor>();
        string frase = await tradutor.TraduzirFraseAsync(
            new PedidoTraducaoFrase("O banco fecha às quatro.", "pt", "en", Anteriores: ["Preciso pagar contas."], Seguintes: []), ct);
        string palavra = await tradutor.TraduzirPalavraAsync(
            new PedidoTraducaoPalavra("banco", Frase: "O banco foi pintado de azul.", Origem: "pt", Destino: "en", TraducaoDaFrase: "The bench was painted blue."), ct);
        Console.WriteLine(frase + palavra);
    }

    // Seção 5.3.
    internal static async Task TempoReal(IServiceProvider sp)
    {
        var tempoReal = sp.GetRequiredService<ITranscritorTempoReal>();
        using var parar = new CancellationTokenSource();
        var sessao = new OpcoesSessaoTempoReal(Idioma: "pt", Contexto: "Dona Chica, Luis Barcellos");
        await foreach (var e in tempoReal.IniciarAsync(Fonte.Microfone, Saida.Ambos, sessao, parar.Token))
        {
            Console.WriteLine($"{e.Tipo} #{e.Id} {e.Texto} {e.Traducao}");
        }

        await using var wav = new FonteAudioWav(@"C:\teste.wav");
        await foreach (var e in tempoReal.IniciarAsync(wav, Saida.Portugues, sessao, parar.Token))
        {
            Console.WriteLine(e.Texto);
        }
    }

    // Seção 5.4.
    internal static async Task Dicionario(CancellationToken ct)
    {
        using var dicionario = DicionarioSqlite.Abrir(@"dados\dicionario.db");
        IReadOnlyList<Verbete> sentidos = dicionario.Procurar("gave", origem: "en", destino: "pt");
        foreach (var v in sentidos)
        {
            Console.WriteLine($"{v.Termo} ({v.Classe}): {v.Traducao} [{v.Sentido}] {v.Lema} {v.Tipo}");
        }

        ResumoImportacao resumo = await ImportadorWiktionary.ImportarAsync(
            @"dados\kaikki-en.jsonl.gz", @"dados\kaikki-pt.jsonl.gz", @"dados\dicionario.db",
            new ListasFrequencia(@"dados\en_50k.txt", @"dados\pt_br_50k.txt"), cancellationToken: ct);
        Console.WriteLine(resumo.TamanhoBytes);
    }

    // Seção 5.5.
    internal static async Task Correcao(IServiceProvider sp, CancellationToken ct)
    {
        var corretor = sp.GetRequiredService<ICorretorTranscricao>();
        IReadOnlyList<Correcao> trocas = await corretor.CorrigirAsync(
            new PedidoCorrecao("The night rode through the forest.".Split(' '), "en", Anteriores: [], Seguintes: []), ct);
        foreach (var t in trocas)
        {
            Console.WriteLine($"{t.Indice}: {t.Ouvida} → {t.Corrigida}");
        }

        var palavra = new Palavra("knight", 0.3, 0.6, Confianca: 0.51, Ouvida: "night");
        Console.WriteLine(palavra.Ouvida);
    }
}
