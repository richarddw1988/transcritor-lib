using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Transcritor.Captura;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.TempoReal;

namespace Transcritor.Tests;

internal sealed class FakeFonte(int blocos) : IFonteAudio
{
    public bool Descartada { get; private set; }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CapturarAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < blocos && !cancellationToken.IsCancellationRequested; i++)
        {
            await Task.Yield();
            yield return new byte[3200];
        }
    }

    public ValueTask DisposeAsync()
    {
        Descartada = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Motor falso: consome todo o áudio e devolve os eventos do roteiro.</summary>
internal sealed class FakeMotorTempoReal(params EventoTempoReal[] roteiro) : IMotorTempoReal
{
    public int BlocosLidos { get; private set; }

    public async IAsyncEnumerable<EventoTempoReal> TranscreverAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio, OpcoesSessaoTempoReal sessao, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in audio.WithCancellation(cancellationToken))
        {
            BlocosLidos++;
        }

        foreach (var e in roteiro)
        {
            yield return e;
        }
    }
}

internal sealed class FakeTradutorComContexto : ITradutor
{
    public List<PedidoTraducaoFrase> Pedidos { get; } = [];

    public string Identificador => "fake";

    public Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken)
    {
        Pedidos.Add(pedido);
        return Task.FromResult($"<{pedido.Destino}> {pedido.Texto}");
    }

    public Task<IReadOnlyList<string>> TraduzirFrasesAsync(IReadOnlyList<string> frases, string origem, string destino, IProgress<double>? progresso, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Tradutor que registra os preparos pedidos pelo tempo real.</summary>
internal sealed class FakeTradutorPreparavel(bool falhar = false) : ITradutor, IPreparavelTempoReal
{
    private readonly object _trava = new();
    private readonly List<bool> _preparos = [];

    public IReadOnlyList<bool> Preparos
    {
        get
        {
            lock (_trava)
            {
                return [.. _preparos];
            }
        }
    }

    public string Identificador => "fake";

    public Task PrepararTempoRealAsync(bool carregarModelo, CancellationToken cancellationToken)
    {
        lock (_trava)
        {
            _preparos.Add(carregarModelo);
        }

        return falhar ? throw new Transcritor.Core.Erros.ServicoIndisponivelException("fora do ar") : Task.CompletedTask;
    }

    public Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken) =>
        Task.FromResult("traduzido");

    public Task<IReadOnlyList<string>> TraduzirFrasesAsync(IReadOnlyList<string> frases, string origem, string destino, IProgress<double>? progresso, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public class TranscritorTempoRealTests
{
    private static EventoTempoReal Prov(long id, string texto) => new(TipoEvento.Provisorio, id, texto, null, "en", 0, 1);

    private static EventoTempoReal Conf(long id, string texto, string? idioma = "en") => new(TipoEvento.Confirmado, id, texto, null, idioma, 0, 1);

    private static TranscritorTempoReal Criar(IMotorTempoReal motor, ITradutor? tradutor = null) =>
        new(motor, Options.Create(new OpcoesTempoReal { TrechosContexto = 1 }), null, tradutor);

    private static async Task<List<EventoTempoReal>> Coletar(IAsyncEnumerable<EventoTempoReal> eventos)
    {
        var lista = new List<EventoTempoReal>();
        await foreach (var e in eventos)
        {
            lista.Add(e);
        }

        return lista;
    }

    [Fact]
    public async Task Ambos_reemite_o_confirmado_com_a_traducao_e_mesmo_id()
    {
        var tradutor = new FakeTradutorComContexto();
        var motor = new FakeMotorTempoReal(Prov(1, "Hello"), Conf(1, "Hello."), Conf(2, "Bye."), Conf(3, "See you."));

        var eventos = await Coletar(Criar(motor, tradutor).IniciarAsync(new FakeFonte(4), Saida.Ambos));

        Assert.Equal(4, motor.BlocosLidos);
        Assert.Equal(4, eventos.Count(e => e.Traducao is null));
        var traduzidos = eventos.Where(e => e.Traducao is not null).ToList();
        Assert.Equal([1L, 2L, 3L], traduzidos.Select(e => e.Id));
        Assert.All(traduzidos, e => Assert.Equal(TipoEvento.Confirmado, e.Tipo));
        Assert.Equal("<pt> Bye.", traduzidos[1].Traducao);
        Assert.Equal("Bye.", traduzidos[1].Texto);

        // Cada trecho leva o anterior como contexto (TrechosContexto = 1).
        Assert.Empty(tradutor.Pedidos[0].Anteriores);
        Assert.Equal(["Hello."], tradutor.Pedidos[1].Anteriores);
        Assert.Equal(["Bye."], tradutor.Pedidos[2].Anteriores);

        // A tradução de um trecho vem depois do próprio trecho.
        Assert.True(eventos.FindIndex(e => e.Id == 2 && e.Traducao is null) < eventos.FindIndex(e => e.Id == 2 && e.Traducao is not null));
    }

    private static async Task<IReadOnlyList<bool>> EsperarPreparos(FakeTradutorPreparavel tradutor, int quantos)
    {
        for (var i = 0; i < 200 && tradutor.Preparos.Count < quantos; i++)
        {
            await Task.Delay(10);
        }

        return tradutor.Preparos;
    }

    [Fact]
    public async Task Ambos_liga_o_tradutor_e_carrega_o_modelo_ao_abrir_a_sessao()
    {
        var tradutor = new FakeTradutorPreparavel();
        await Coletar(Criar(new FakeMotorTempoReal(Conf(1, "Hi.")), tradutor).IniciarAsync(new FakeFonte(1), Saida.Ambos));

        var preparos = await EsperarPreparos(tradutor, 2);
        Assert.Equal(2, preparos.Count);
        Assert.Contains(false, preparos); // liga o contêiner
        Assert.Contains(true, preparos);  // carrega o modelo
    }

    [Fact]
    public async Task Saida_num_idioma_so_liga_o_tradutor_e_falha_no_preparo_nao_derruba_a_sessao()
    {
        var tradutor = new FakeTradutorPreparavel(falhar: true);
        var eventos = await Coletar(Criar(new FakeMotorTempoReal(Conf(1, "Oi.", idioma: "pt")), tradutor).IniciarAsync(new FakeFonte(1), Saida.Ingles));

        Assert.Equal("traduzido", eventos.Single(e => e.Traducao is not null).Traducao);
        await EsperarPreparos(tradutor, 1);
        await Task.Delay(50);
        Assert.Equal([false], tradutor.Preparos);
    }

    [Fact]
    public async Task Saida_no_idioma_falado_nao_traduz()
    {
        var tradutor = new FakeTradutorComContexto();
        var eventos = await Coletar(Criar(new FakeMotorTempoReal(Conf(1, "Hi.")), tradutor).IniciarAsync(new FakeFonte(1), Saida.Ingles));

        Assert.Single(eventos);
        Assert.Empty(tradutor.Pedidos);
    }

    [Fact]
    public async Task Idioma_desconhecido_nao_traduz()
    {
        var tradutor = new FakeTradutorComContexto();
        var eventos = await Coletar(Criar(new FakeMotorTempoReal(Conf(1, "Hi.", idioma: null)), tradutor).IniciarAsync(new FakeFonte(1), Saida.Ambos));

        Assert.Single(eventos);
        Assert.Empty(tradutor.Pedidos);
    }

    [Fact]
    public async Task Pedir_traducao_sem_tradutor_explica_o_que_falta()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Coletar(Criar(new FakeMotorTempoReal(Conf(1, "Hi."))).IniciarAsync(new FakeFonte(1), Saida.Portugues)));
        Assert.Contains("AddTranscritorTraducao", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parar_encerra_a_captura_e_a_sessao_termina_sem_excecao()
    {
        using var parar = new CancellationTokenSource();
        var motor = new FakeMotorTempoReal(Conf(1, "Fim."));
        var fonte = new FakeFonte(int.MaxValue);
        var enumeracao = Criar(motor).IniciarAsync(fonte, Saida.Ingles, cancellationToken: parar.Token);

        var tarefa = Coletar(enumeracao);
        await Task.Delay(50);
        await parar.CancelAsync();
        var eventos = await tarefa.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(motor.BlocosLidos > 0);
        Assert.Equal("Fim.", Assert.Single(eventos).Texto);
    }

    [Fact]
    public async Task Fonte_criada_pela_fabrica_e_descartada_no_fim()
    {
        var fonte = new FakeFonte(1);
        var transcritor = new TranscritorTempoReal(
            new FakeMotorTempoReal(), Options.Create(new OpcoesTempoReal()), new FabricaUnica(fonte));

        await Coletar(transcritor.IniciarAsync(Fonte.Microfone, Saida.Ingles));

        Assert.True(fonte.Descartada);
    }

    [Fact]
    public void Sem_fabrica_de_fontes_explica_o_que_falta()
    {
        var transcritor = Criar(new FakeMotorTempoReal());
        var ex = Assert.Throws<InvalidOperationException>(() => transcritor.IniciarAsync(Fonte.Microfone, Saida.Ingles));
        Assert.Contains("AddTranscritorCaptura", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FabricaUnica(IFonteAudio fonte) : IFabricaFonteAudio
    {
        public IFonteAudio Criar(Fonte tipo) => fonte;
    }
}

public sealed class FonteAudioWavTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private string Wav(int amostras, int taxa = 16000, short canais = 1)
    {
        var caminho = Path.Combine(_pasta.Caminho, $"{Guid.NewGuid():N}.wav");
        using var w = new BinaryWriter(File.Create(caminho));
        var dados = amostras * 2 * canais;
        w.Write("RIFF"u8); w.Write(36 + dados); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(canais); w.Write(taxa); w.Write(taxa * 2 * canais); w.Write((short)(2 * canais)); w.Write((short)16);
        w.Write("data"u8); w.Write(dados);
        for (var i = 0; i < amostras * canais; i++)
        {
            w.Write((short)i);
        }

        return caminho;
    }

    [Fact]
    public async Task Entrega_blocos_de_100ms()
    {
        var blocos = new List<int>();
        await foreach (var b in new FonteAudioWav(Wav(4000), velocidade: 0).CapturarAsync(CancellationToken.None))
        {
            blocos.Add(b.Length);
        }

        Assert.Equal([3200, 3200, 1600], blocos);
    }

    [Fact]
    public async Task Formato_errado_gera_erro_claro()
    {
        var ex = await Assert.ThrowsAsync<Transcritor.Core.Erros.TranscritorException>(async () =>
        {
            await foreach (var _ in new FonteAudioWav(Wav(100, taxa: 44100, canais: 2), 0).CapturarAsync(CancellationToken.None))
            {
            }
        });
        Assert.Contains("16 kHz mono", ex.Message, StringComparison.Ordinal);
    }
}

public class ConversaoPcmTests
{
    [Fact]
    public void Mistura_canais_pela_media()
    {
        var mono = new float[2];
        ConversaoPcm.MisturarParaMono([1f, 0f, 0.5f, -0.5f], 2, mono);
        Assert.Equal([0.5f, 0f], mono);
    }

    [Fact]
    public void Converte_float_em_pcm16_com_saturacao()
    {
        var bytes = new byte[8];
        ConversaoPcm.FloatParaPcm16([0f, 1f, -1f, 2f], bytes);
        var amostras = Enumerable.Range(0, 4).Select(i => BitConverter.ToInt16(bytes, i * 2)).ToArray();
        Assert.Equal([(short)0, short.MaxValue, (short)-short.MaxValue, short.MaxValue], amostras);
    }
}
