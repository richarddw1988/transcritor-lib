using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Transcritor.Core.Contratos;
using Transcritor.Dicionario;
using Transcritor.Traducao;
using Transcritor.Traducao.Correcoes;
using Transcritor.Traducao.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Tests;

public sealed class HomofonosTests : IDisposable
{
    // Linhas no formato do kaikki.org; "bare" não tem tradução para o português (passa pelo filtro só pelo homófono).
    private const string Ingles = """
        {"word": "bare", "pos": "adj", "lang_code": "en", "senses": [{"glosses": ["naked"]}], "sounds": [{"homophone": "bear"}, {"homophone": "burr (fair–fur merger)"}, {"homophone": "Bayer"}]}
        {"word": "night", "pos": "noun", "lang_code": "en", "senses": [{"glosses": ["x"]}], "translations": [{"code": "pt", "word": "noite"}], "sounds": [{"homophone": "knight"}, {"homophone": "nite"}]}
        """;

    private const string Portugues = """
        {"word": "cela", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["cell"]}], "sounds": [{"homophone": "sela"}]}
        {"word": "sessão", "pos": "noun", "lang_code": "pt", "senses": [{"glosses": ["session"]}], "sounds": [{"homophone": "cessão"}, {"homophone": "(Brazil) seção"}, {"homophone": "(Portugal) secção"}]}
        """;

    private readonly PastaTemporaria _pasta = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _pasta.Dispose();
    }

    private async Task<(string Banco, ResumoImportacao Resumo)> ImportarAsync()
    {
        var banco = Path.Combine(_pasta.Caminho, "dic.db");
        var resumo = await ImportadorWiktionary.ImportarAsync(_pasta.Arquivo("en.jsonl", Ingles), _pasta.Arquivo("pt.jsonl", Portugues), banco);
        return (banco, resumo);
    }

    [Fact]
    public async Task Importa_os_homofonos_nos_dois_sentidos_sem_ressalva_nem_nome_proprio()
    {
        var (banco, resumo) = await ImportarAsync();
        using var dicionario = DicionarioSqlite.Abrir(banco);

        Assert.True(dicionario.TemHomofonos);
        Assert.Equal(["bear"], dicionario.Homofonos("Bare,", "en"));
        Assert.Equal(["bare"], dicionario.Homofonos("bear", "en"));
        Assert.Equal(["knight", "nite"], dicionario.Homofonos("night", "en"));
        Assert.Equal(["sela"], dicionario.Homofonos("cela", "pt"));
        Assert.Equal(["cela"], dicionario.Homofonos("sela", "pt"));
        Assert.Equal(["cessão", "seção"], dicionario.Homofonos("sessão", "pt"));
        Assert.Empty(dicionario.Homofonos("sela", "en"));
        Assert.Empty(dicionario.Homofonos("burr", "en"));
        Assert.Equal(12, resumo.Homofonos);
    }

    [Fact]
    public async Task Banco_antigo_sem_a_lista_funciona_sem_homofonos()
    {
        var (banco, _) = await ImportarAsync();
        await using (var conexao = new SqliteConnection($"Data Source={banco};Pooling=False"))
        {
            await conexao.OpenAsync();
            using var cmd = conexao.CreateCommand();
            cmd.CommandText = "DROP TABLE homofono";
            await cmd.ExecuteNonQueryAsync();
        }

        using var dicionario = DicionarioSqlite.Abrir(banco);

        Assert.False(dicionario.TemHomofonos);
        Assert.Empty(dicionario.Homofonos("cela", "pt"));
    }

    private static readonly FakeHttpHandler SemOllama = new((_, _) => throw new InvalidOperationException("não deveria chamar o Ollama"));

    private static CorretorOllama Corretor(
        IEscolhedorHomofonos? escolhedor, string banco, bool homofonos = true, FakeHttpHandler? handler = null, bool modelo = true,
        bool conferirComBert = false, bool homofonoComModelo = false)
    {
        var opcoes = Options.Create(new OpcoesTraducao
        {
            CorrigirTranscricao = modelo,
            ConferirCorrecaoComBert = conferirComBert,
            ConfirmarHomofonoComModelo = homofonoComModelo,
            ModeloCorrecao = "gemma3:12b",
            CaminhoDicionario = banco,
            CorrigirHomofonos = homofonos,
        });
        var http = new HttpClient(handler ?? SemOllama) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return new CorretorOllama(new ClienteOllama(http, opcoes), opcoes, new FonteDicionario(opcoes), escolhedor);
    }

    /// <summary>Escolhedor falso: dá <paramref name="probabilidade"/> à <paramref name="grafia"/> e o resto às outras.</summary>
    private sealed class Escolhe(string grafia, double probabilidade, bool disponivel = true) : IEscolhedorHomofonos
    {
        public List<(string Frase, int Indice, string[] Grafias)> Pedidos { get; } = [];

        public bool Disponivel(string idioma) => disponivel;

        public IReadOnlyList<GrafiaProvavel> Avaliar(IReadOnlyList<string> palavras, int indice, IReadOnlyList<string> grafias, string idioma, bool lacunaNasRepetidas = false)
        {
            Pedidos.Add((string.Join(' ', palavras), indice, grafias.ToArray()));
            var outras = grafias.Where(g => g != grafia).ToList();
            return [new(grafia, probabilidade), .. outras.Select(g => new GrafiaProvavel(g, (1 - probabilidade) / outras.Count))];
        }
    }

    /// <summary>"A cela do cavalo estava rasgada." com o reconhecimento seguro em todas as palavras.</summary>
    private static readonly PedidoCorrecao Confiante = new(
        "A cela do cavalo estava rasgada.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0]);

    [Fact]
    public async Task Homofono_seguro_vai_ao_bert_e_a_troca_certa_sai_marcada()
    {
        var (banco, _) = await ImportarAsync();
        var bert = new Escolhe("sela", 0.997);
        var corretor = Corretor(bert, banco);

        var trocas = await corretor.CorrigirAsync(Confiante, CancellationToken.None);

        // Todas as palavras seguras: o Ollama nem é chamado; só o BERT, com a ouvida e os homófonos.
        Assert.Equal(new Correcao(1, "cela", "sela", Homofono: true), Assert.Single(trocas));
        var (frase, indice, grafias) = Assert.Single(bert.Pedidos);
        Assert.Equal(("A cela do cavalo estava rasgada.", 1), (frase, indice));
        Assert.Equal(["cela", "sela"], grafias);
        Assert.Contains("-h4:0.99", corretor.Identificador, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cela", 0.999)] // o BERT prefere a ouvida
    [InlineData("sela", 0.98)]  // prefere outra, mas abaixo do limite (0,99)
    public async Task Sem_certeza_alta_o_homofono_fica(string grafia, double probabilidade)
    {
        var (banco, _) = await ImportarAsync();

        Assert.Empty(await Corretor(new Escolhe(grafia, probabilidade), banco).CorrigirAsync(Confiante, CancellationToken.None));
    }

    [Fact]
    public async Task Pontuacao_fica_e_nome_no_meio_da_frase_nao_passa_pelo_bert()
    {
        var (banco, _) = await ImportarAsync();
        var bert = new Escolhe("sela", 0.999);
        var pedido = new PedidoCorrecao("Cela ficou na cela.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0, 1.0]);
        var nome = new PedidoCorrecao("Falou com Cela ontem.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0, 1.0]);

        var trocas = await Corretor(bert, banco).CorrigirAsync(pedido, CancellationToken.None);

        Assert.Equal([new Correcao(0, "Cela", "Sela", Homofono: true), new Correcao(3, "cela.", "sela.", Homofono: true)], trocas);
        Assert.Empty(await Corretor(bert, banco).CorrigirAsync(nome, CancellationToken.None));
    }

    [Fact]
    public async Task Com_a_correcao_pelo_modelo_desligada_so_os_homofonos_rodam()
    {
        var (banco, _) = await ImportarAsync();
        // "rasgada" com confiança baixa iria ao modelo de conversa; com ele desligado, o Ollama não é chamado.
        var duvidosa = Confiante with { Confiancas = [1.0, 1.0, 1.0, 1.0, 1.0, 0.3] };
        var corretor = Corretor(new Escolhe("sela", 0.999), banco, modelo: false);

        Assert.True(corretor.Ativo);
        Assert.Equal("so-h4:0.99", corretor.Identificador);
        Assert.Equal(new Correcao(1, "cela", "sela", Homofono: true), Assert.Single(await corretor.CorrigirAsync(duvidosa, CancellationToken.None)));
        await corretor.VerificarAsync(CancellationToken.None); // sem modelo de conversa, não confere o Ollama
        Assert.False(Corretor(new Escolhe("sela", 0.999, disponivel: false), banco, modelo: false).Ativo);
        Assert.False(Corretor(new Escolhe("sela", 0.999), banco, homofonos: false, modelo: false).Ativo);
    }

    /// <summary>Ollama falso que devolve <paramref name="corrigida"/> como a frase corrigida.</summary>
    private static FakeHttpHandler Corrige(string corrigida) => new((_, _) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(
            new { message = new { role = "assistant", content = $"{{\"corrigida\": \"{corrigida}\"}}" }, done = true })));

    [Fact]
    public async Task Bert_barra_a_troca_do_modelo_quando_prefere_a_ouvida()
    {
        // en22: o modelo trocou "leave" (certa) por "live"; o BERT dá só 0,1 a "live" ali.
        var (banco, _) = await ImportarAsync();
        var pedido = new PedidoCorrecao("We live near the station, but we leave early.".Split(' '), "en", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.6, 1.0]);
        var bert = new Escolhe("live", 0.1);

        var trocas = await Corretor(bert, banco, handler: Corrige("We live near the station, but we live early."), conferirComBert: true)
            .CorrigirAsync(pedido, CancellationToken.None);

        Assert.Empty(trocas);
        var (frase, indice, grafias) = Assert.Single(bert.Pedidos);
        Assert.Equal(7, indice);
        Assert.Equal(["leave", "live"], grafias);
        Assert.Contains("we leave early", frase, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Troca_do_modelo_que_o_bert_aprova_vale_e_sem_a_opcao_o_bert_nao_e_chamado()
    {
        var (banco, _) = await ImportarAsync();
        // en03: "chop" (0,34) → "shop".
        var pedido = new PedidoCorrecao("She bought a pen at the chop.".Split(' '), "en", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.34]);
        var aprova = new Escolhe("shop", 0.95);
        var semConferir = new Escolhe("shop", 0.0);

        var conferida = await Corretor(aprova, banco, handler: Corrige("She bought a pen at the shop."), conferirComBert: true)
            .CorrigirAsync(pedido, CancellationToken.None);
        var livre = await Corretor(semConferir, banco, handler: Corrige("She bought a pen at the shop."))
            .CorrigirAsync(pedido, CancellationToken.None);

        Assert.Equal(new Correcao(6, "chop.", "shop."), Assert.Single(conferida));
        Assert.Equal(new Correcao(6, "chop.", "shop."), Assert.Single(livre));
        Assert.Empty(semConferir.Pedidos);
        Assert.Contains("-cb:0.5", Corretor(aprova, banco, conferirComBert: true).Identificador, StringComparison.Ordinal);
    }

    /// <summary>Ollama falso que escolhe a opção que contém <paramref name="grafia"/> (ou sempre a 1, se nulo); conta as perguntas.</summary>
    private sealed class Escolhedor(string? grafia)
    {
        public int Perguntas { get; private set; }

        public FakeHttpHandler Handler => new((_, corpo) =>
        {
            Perguntas++;
            var conteudo = JsonDocument.Parse(corpo).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            var opcao1 = conteudo.Split('\n')[0];
            var escolha = grafia is null || opcao1.Contains(grafia, StringComparison.Ordinal) ? 1 : 2;
            return FakeHttpHandler.Json(JsonSerializer.Serialize(
                new { message = new { role = "assistant", content = $"{{\"escolha\": {escolha}}}" }, done = true }));
        });
    }

    [Fact]
    public async Task Homofono_duvidoso_vai_ao_modelo_e_so_troca_se_ele_escolher_a_outra_nas_duas_ordens()
    {
        var (banco, _) = await ImportarAsync();
        // O BERT acha "sela" pouco provável (0,05: abaixo de 0,99, acima de 0,02); o modelo de conversa decide.
        var bert = new Escolhe("cela", 0.95);
        var coerente = new Escolhedor("sela");
        var semPreferencia = new Escolhedor(null); // sempre a opção 1: muda de lado quando a ordem troca

        var trocas = await Corretor(bert, banco, handler: coerente.Handler, modelo: false, homofonoComModelo: true)
            .CorrigirAsync(Confiante, CancellationToken.None);
        var mantidas = await Corretor(bert, banco, handler: semPreferencia.Handler, modelo: false, homofonoComModelo: true)
            .CorrigirAsync(Confiante, CancellationToken.None);

        Assert.Equal(new Correcao(1, "cela", "sela", Homofono: true), Assert.Single(trocas));
        Assert.Equal(2, coerente.Perguntas);
        Assert.Empty(mantidas);
        Assert.Contains("-hm2:0.02", Corretor(bert, banco, modelo: false, homofonoComModelo: true).Identificador, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duas_grafias_vencem_a_ouvida_e_fica_a_que_ganha_da_outra()
    {
        var (banco, _) = await ImportarAsync();
        // "sessão" tem dois homófonos; o BERT dá 0,05 a cada. O modelo falso prefere seção > cessão > sessão: as duas
        // ganham da ouvida, e "seção" ganha de "cessão".
        string[] ordem = ["seção", "cessão", "sessão"];
        int Posto(string opcao) => Array.FindIndex(ordem, g => opcao.Contains($" {g} ", StringComparison.Ordinal));
        var ranqueado = new FakeHttpHandler((_, corpo) =>
        {
            var linhas = JsonDocument.Parse(corpo).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Split('\n');
            var escolha = Posto(linhas[0]) <= Posto(linhas[1]) ? 1 : 2;
            return FakeHttpHandler.Json(JsonSerializer.Serialize(
                new { message = new { role = "assistant", content = $"{{\"escolha\": {escolha}}}" }, done = true }));
        });
        var pedido = new PedidoCorrecao("A sessão de esportes do jornal.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0]);

        var trocas = await Corretor(new Escolhe("sessão", 0.9), banco, handler: ranqueado, modelo: false, homofonoComModelo: true)
            .CorrigirAsync(pedido, CancellationToken.None);

        Assert.Equal(new Correcao(1, "sessão", "seção", Homofono: true), Assert.Single(trocas));
    }

    [Fact]
    public async Task Homofono_que_o_bert_descarta_ou_com_a_opcao_desligada_nao_vai_ao_modelo()
    {
        var (banco, _) = await ImportarAsync();

        // SemOllama lança se for chamado.
        Assert.Empty(await Corretor(new Escolhe("cela", 0.99), banco, modelo: false, homofonoComModelo: true)
            .CorrigirAsync(Confiante, CancellationToken.None)); // "sela" com 0,01, abaixo de 0,02
        Assert.Empty(await Corretor(new Escolhe("cela", 0.95), banco, modelo: false)
            .CorrigirAsync(Confiante, CancellationToken.None));
        Assert.DoesNotContain("-hm", Corretor(new Escolhe("cela", 0.95), banco, modelo: false).Identificador, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Com_a_conferencia_ligada_e_sem_o_bert_do_idioma_o_modelo_nao_roda()
    {
        var (banco, _) = await ImportarAsync();
        var pedido = new PedidoCorrecao("She bought a pen at the chop.".Split(' '), "en", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.34]);
        var semBert = Corretor(new Escolhe("shop", 0.95, disponivel: false), banco, conferirComBert: true);
        var semEscolhedor = Corretor(null, banco, conferirComBert: true);

        // SemOllama lança se for chamado: nem a frase duvidosa vai ao modelo, nem o Ollama é conferido.
        Assert.Empty(await semBert.CorrigirAsync(pedido, CancellationToken.None));
        Assert.Empty(await semEscolhedor.CorrigirAsync(pedido, CancellationToken.None));
        await semBert.VerificarAsync(CancellationToken.None);
        Assert.False(semBert.Ativo);
        Assert.False(semEscolhedor.Ativo);
        Assert.Equal("so", semEscolhedor.Identificador);
    }

    [Fact]
    public async Task Desligado_ou_sem_dicionario_o_homofono_seguro_nao_vai_ao_modelo()
    {
        var (banco, _) = await ImportarAsync();
        var bert = new Escolhe("sela", 0.999);

        Assert.Empty(await Corretor(bert, banco, homofonos: false).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(await Corretor(bert, Path.Combine(_pasta.Caminho, "nao.db")).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(await Corretor(null, banco).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(await Corretor(new Escolhe("sela", 0.999, disponivel: false), banco).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(bert.Pedidos);
        Assert.DoesNotContain("-h", Corretor(bert, banco, homofonos: false).Identificador, StringComparison.Ordinal);
        Assert.DoesNotContain("-h", Corretor(null, banco).Identificador, StringComparison.Ordinal);
    }
}
