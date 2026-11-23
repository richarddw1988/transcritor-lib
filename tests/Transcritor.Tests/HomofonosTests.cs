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

    private static CorretorOllama Corretor(FakeHttpHandler handler, string banco, bool homofonos = true)
    {
        var opcoes = Options.Create(new OpcoesTraducao
        {
            CorrigirTranscricao = true,
            ModeloCorrecao = "gemma3:12b",
            CaminhoDicionario = banco,
            CorrigirHomofonos = homofonos,
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        return new CorretorOllama(new ClienteOllama(http, opcoes), opcoes, new FonteDicionario(opcoes));
    }

    /// <summary>Responde a pergunta do homófono com <paramref name="palavra"/> e a 2ª opinião com <paramref name="confirma"/>.</summary>
    private static FakeHttpHandler Escolhe(string palavra, int confirma = 2) => new((_, corpo) =>
        FakeHttpHandler.Json(JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = corpo.Contains("opcao2", StringComparison.Ordinal) ? $"{{\"escolha\": {confirma}}}" : $"{{\"palavra\": \"{palavra}\"}}",
            },
            done = true,
        })));

    /// <summary>"A cela do cavalo estava rasgada." com o reconhecimento seguro em todas as palavras.</summary>
    private static readonly PedidoCorrecao Confiante = new(
        "A cela do cavalo estava rasgada.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0, 1.0, 1.0, 1.0]);

    [Fact]
    public async Task Homofono_seguro_vira_pergunta_com_lacuna_e_a_troca_sai_marcada()
    {
        var (banco, _) = await ImportarAsync();
        var handler = Escolhe("sela");
        var corretor = Corretor(handler, banco);

        var trocas = await corretor.CorrigirAsync(Confiante, CancellationToken.None);

        // Todas as palavras seguras: a correção principal nem é chamada; vão a pergunta do homófono e a 2ª opinião.
        Assert.Equal(new Correcao(1, "cela", "sela", Homofono: true), Assert.Single(trocas));
        Assert.Equal(2, handler.Recebidas.Count);
        using var pedido = JsonDocument.Parse(handler.Recebidas[0].Corpo);
        Assert.Equal("gemma3:12b", pedido.RootElement.GetProperty("model").GetString());
        var usuario = pedido.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"frase\":\"A ___ do cavalo estava rasgada.\"", usuario, StringComparison.Ordinal);
        Assert.Contains("\"opcoes\":[\"cela\",\"sela\"]", usuario, StringComparison.Ordinal);
        Assert.Contains("-h2:gemma3:12b", corretor.Identificador, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Homofono_que_o_modelo_mantem_nao_e_trocado_e_a_pontuacao_fica()
    {
        var (banco, _) = await ImportarAsync();
        var pedido = new PedidoCorrecao("Ficou na cela.".Split(' '), "pt", [], [], [1.0, 1.0, 1.0]);

        Assert.Empty(await Corretor(Escolhe("cela"), banco).CorrigirAsync(pedido, CancellationToken.None));
        Assert.Empty(await Corretor(Escolhe("sela", confirma: 1), banco).CorrigirAsync(pedido, CancellationToken.None));
        Assert.Equal(
            new Correcao(2, "cela.", "sela.", Homofono: true),
            Assert.Single(await Corretor(Escolhe("sela"), banco).CorrigirAsync(pedido, CancellationToken.None)));
    }

    [Fact]
    public async Task Desligado_ou_sem_dicionario_o_homofono_seguro_nao_vai_ao_modelo()
    {
        var (banco, _) = await ImportarAsync();
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("não deveria chamar"));

        Assert.Empty(await Corretor(handler, banco, homofonos: false).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(await Corretor(handler, Path.Combine(_pasta.Caminho, "nao.db")).CorrigirAsync(Confiante, CancellationToken.None));
        Assert.Empty(handler.Recebidas);
        Assert.DoesNotContain("-h", Corretor(handler, banco, homofonos: false).Identificador, StringComparison.Ordinal);
    }
}
