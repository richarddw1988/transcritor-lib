using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.TempoReal;
using Transcritor.TempoReal.WebSockets;
using Transcritor.TempoReal.WhisperLiveKit;

namespace Transcritor.Tests;

/// <summary>Servidor falso: manda "config", responde a cada bloco de áudio com a próxima mensagem do roteiro e,
/// quando recebe o frame vazio, manda o resto do roteiro e "ready_to_stop".</summary>
internal sealed class FakeConexaoWlk(IReadOnlyList<string> roteiro, bool usaPcm = true, Exception? erroAoConectar = null) : IConexaoWebSocket
{
    private readonly Channel<string?> _saida = Channel.CreateUnbounded<string?>();
    private int _proxima;

    public Uri? Uri { get; private set; }
    public List<int> BlocosRecebidos { get; } = [];
    public bool RecebeuFim { get; private set; }

    public Task ConectarAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (erroAoConectar is not null)
        {
            throw erroAoConectar;
        }

        Uri = uri;
        _saida.Writer.TryWrite($$"""{"type":"config","useAudioWorklet":{{(usaPcm ? "true" : "false")}},"mode":"full"}""");
        return Task.CompletedTask;
    }

    public Task EnviarBinarioAsync(ReadOnlyMemory<byte> dados, CancellationToken cancellationToken)
    {
        if (dados.IsEmpty)
        {
            RecebeuFim = true;
            while (_proxima < roteiro.Count)
            {
                _saida.Writer.TryWrite(roteiro[_proxima++]);
            }

            _saida.Writer.TryWrite("""{"type":"ready_to_stop"}""");
            _saida.Writer.TryComplete();
            return Task.CompletedTask;
        }

        BlocosRecebidos.Add(dados.Length);
        if (_proxima < roteiro.Count)
        {
            _saida.Writer.TryWrite(roteiro[_proxima++]);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReceberTextoAsync(CancellationToken cancellationToken)
    {
        if (await _saida.Reader.WaitToReadAsync(cancellationToken) && _saida.Reader.TryRead(out var msg))
        {
            return msg;
        }

        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FabricaFake(IConexaoWebSocket conexao) : IFabricaConexaoWebSocket
{
    public IConexaoWebSocket Criar() => conexao;
}

public class MotorWhisperLiveKitTests
{
    private static string Estado(string buffer, params (string Texto, string Inicio, string Fim)[] linhas)
    {
        var ls = string.Join(",", linhas.Select(l => l.Texto == "-"
            ? $$"""{"speaker":-2,"text":null,"start":"{{l.Inicio}}","end":"{{l.Fim}}"}"""
            : $$"""{"speaker":1,"text":"{{l.Texto}}","start":"{{l.Inicio}}","end":"{{l.Fim}}","detected_language":"en"}"""));
        return $$"""{"status":"active_transcription","lines":[{{ls}}],"buffer_transcription":"{{buffer}}"}""";
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio(int blocos, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < blocos; i++)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new byte[3200];
        }
    }

    private static MotorWhisperLiveKit Criar(IConexaoWebSocket conexao, Action<OpcoesTempoReal>? ajustar = null)
    {
        var o = new OpcoesTempoReal { WhisperLiveKitUrl = new Uri("ws://wlk.test:8001/asr"), TimeoutFinalizacao = TimeSpan.FromSeconds(5) };
        ajustar?.Invoke(o);
        return new MotorWhisperLiveKit(new FabricaFake(conexao), Options.Create(o));
    }

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
    public async Task Envia_audio_e_fim_e_gera_provisorio_e_confirmado()
    {
        var conexao = new FakeConexaoWlk([
            Estado(" Hello", ("", "0:00:00.00", "0:00:00.00")),
            Estado(" are", ("Hello how", "0:00:00.00", "0:00:01.00")),
            Estado("", ("Hello how are you.", "0:00:00.00", "0:00:02.00"), ("-", "0:00:02.00", "0:00:03.00")),
        ]);

        var eventos = await Coletar(Criar(conexao).TranscreverAsync(Audio(3), new OpcoesSessaoTempoReal(), CancellationToken.None));

        Assert.Equal("ws://wlk.test:8001/asr?language=auto&mode=full", conexao.Uri!.ToString());
        Assert.Equal([3200, 3200, 3200], conexao.BlocosRecebidos);
        Assert.True(conexao.RecebeuFim);
        Assert.Contains(eventos, e => e.Tipo == TipoEvento.Provisorio && e.Texto == "Hello how are");
        var confirmado = Assert.Single(eventos, e => e.Tipo == TipoEvento.Confirmado);
        Assert.Equal("Hello how are you.", confirmado.Texto);
        Assert.Equal(2.0, confirmado.Fim);
    }

    [Fact]
    public async Task Idioma_forcado_vai_na_url()
    {
        var conexao = new FakeConexaoWlk([]);
        await Coletar(Criar(conexao).TranscreverAsync(Audio(1), new OpcoesSessaoTempoReal("pt-BR"), CancellationToken.None));

        Assert.Contains("language=pt", conexao.Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fim_da_sessao_confirma_o_texto_pendente()
    {
        var conexao = new FakeConexaoWlk([Estado(" talvez", ("Tudo certo", "0:00:00.00", "0:00:01.00"))]);

        var eventos = await Coletar(Criar(conexao).TranscreverAsync(Audio(1), new OpcoesSessaoTempoReal(), CancellationToken.None));

        Assert.Equal("Tudo certo", Assert.Single(eventos, e => e.Tipo == TipoEvento.Confirmado).Texto);
    }

    [Fact]
    public async Task Servidor_fora_do_ar_gera_erro_dizendo_qual()
    {
        var conexao = new FakeConexaoWlk([], erroAoConectar: new WebSocketException("Unable to connect"));

        var ex = await Assert.ThrowsAsync<ServicoIndisponivelException>(
            () => Coletar(Criar(conexao).TranscreverAsync(Audio(1), new OpcoesSessaoTempoReal(), CancellationToken.None)));
        Assert.Equal("WhisperLiveKit", ex.Servico);
        Assert.Contains("wlk.test:8001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servidor_sem_pcm_input_gera_erro_claro()
    {
        var conexao = new FakeConexaoWlk([], usaPcm: false);

        var ex = await Assert.ThrowsAsync<TranscritorException>(
            () => Coletar(Criar(conexao).TranscreverAsync(Audio(1), new OpcoesSessaoTempoReal(), CancellationToken.None)));
        Assert.Contains("--pcm-input", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erro_do_servidor_vira_excecao()
    {
        var conexao = new FakeConexaoWlk(["""{"type":"error","error":"Session limit reached"}"""]);

        var ex = await Assert.ThrowsAsync<TranscritorException>(
            () => Coletar(Criar(conexao).TranscreverAsync(Audio(2), new OpcoesSessaoTempoReal(), CancellationToken.None)));
        Assert.Contains("Session limit reached", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erro_na_fonte_de_audio_e_relancado()
    {
        static async IAsyncEnumerable<ReadOnlyMemory<byte>> Quebrada()
        {
            await Task.Yield();
            yield return new byte[10];
            throw new IOException("microfone removido");
        }

        var conexao = new FakeConexaoWlk([]);
        var ex = await Assert.ThrowsAsync<IOException>(
            () => Coletar(Criar(conexao).TranscreverAsync(Quebrada(), new OpcoesSessaoTempoReal(), CancellationToken.None)));
        Assert.Equal("microfone removido", ex.Message);
    }
}
