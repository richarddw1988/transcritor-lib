using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.TempoReal.WebSockets;

namespace Transcritor.TempoReal.WhisperLiveKit;

/// <summary>
/// Cliente do WhisperLiveKit (/asr, servidor com --pcm-input). Envia PCM s16le 16 kHz mono em frames binários,
/// manda um frame vazio no fim do áudio e espera "ready_to_stop".
/// </summary>
public sealed class MotorWhisperLiveKit(IFabricaConexaoWebSocket fabrica, IOptions<OpcoesTempoReal> opcoes) : IMotorTempoReal, IServicoVerificavel
{
    internal const string Nome = "WhisperLiveKit";

    private readonly OpcoesTempoReal _opcoes = opcoes.Value;

    public string NomeServico => Nome;

    public async IAsyncEnumerable<EventoTempoReal> TranscreverAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        OpcoesSessaoTempoReal sessao,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sessao);

        var uri = MontarUri(sessao.Idioma);
        await using var conexao = fabrica.Criar();
        await ConectarAsync(conexao, uri, cancellationToken).ConfigureAwait(false);

        using var recebimento = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var envioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var envio = EnviarAsync(conexao, audio, recebimento, envioCts.Token);
        var derivador = new DerivadorEventos(sessao.Idioma);

        try
        {
            var primeira = true;
            while (true)
            {
                var texto = await ReceberAsync(conexao, uri, recebimento.Token, cancellationToken).ConfigureAwait(false);
                if (texto is null)
                {
                    break; // fechou, ou passou do tempo de finalização
                }

                var mensagem = MensagemWlk.Interpretar(texto);
                if (mensagem.EhErro)
                {
                    throw new TranscritorException($"O {Nome} informou um erro: {mensagem.Erro ?? texto}");
                }

                if (mensagem.Tipo == "config")
                {
                    if (primeira && mensagem.UsaPcm == false)
                    {
                        throw new TranscritorException(
                            $"O {Nome} em {uri} não foi iniciado com --pcm-input; a lib envia PCM s16le 16 kHz mono.");
                    }

                    primeira = false;
                    continue;
                }

                primeira = false;
                if (mensagem.Tipo == "ready_to_stop")
                {
                    break;
                }

                if (mensagem.Tipo is null or "snapshot")
                {
                    foreach (var evento in derivador.Processar(mensagem))
                    {
                        yield return evento;
                    }
                }
            }
        }
        finally
        {
            await envioCts.CancelAsync().ConfigureAwait(false);
            await Observar(envio).ConfigureAwait(false);
        }

        // Erro na leitura do áudio (ex.: dispositivo removido) vale mais que um fim "normal".
        if (envio.IsFaulted)
        {
            await envio.ConfigureAwait(false);
        }

        foreach (var evento in derivador.Finalizar())
        {
            yield return evento;
        }
    }

    public async Task VerificarAsync(CancellationToken cancellationToken)
    {
        var uri = MontarUri(null);
        await using var conexao = fabrica.Criar();
        await ConectarAsync(conexao, uri, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_opcoes.TimeoutConexao);
        var texto = await ReceberAsync(conexao, uri, cts.Token, cancellationToken).ConfigureAwait(false)
            ?? throw new ServicoIndisponivelException($"O {Nome} em {uri} aceitou a conexão mas não mandou a configuração.");
        var mensagem = MensagemWlk.Interpretar(texto);
        if (mensagem.Tipo == "config" && mensagem.UsaPcm == false)
        {
            throw new TranscritorException($"O {Nome} em {uri} não foi iniciado com --pcm-input.");
        }

        await conexao.EnviarBinarioAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    internal Uri MontarUri(string? idioma)
    {
        var codigo = string.IsNullOrWhiteSpace(idioma) ? "auto" : Idiomas.Normalizar(idioma);
        var construtor = new UriBuilder(_opcoes.WhisperLiveKitUrl);
        var consulta = construtor.Query.TrimStart('?');
        var extra = $"language={Uri.EscapeDataString(codigo)}&mode=full";
        construtor.Query = string.IsNullOrEmpty(consulta) ? extra : consulta + "&" + extra;
        return construtor.Uri;
    }

    private async Task ConectarAsync(IConexaoWebSocket conexao, Uri uri, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_opcoes.TimeoutConexao);
        try
        {
            await conexao.ConectarAsync(uri, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ServicoIndisponivelException(Nome, uri, ex);
        }
    }

    /// <summary>Nulo se a conexão fechou ou se o prazo de finalização acabou (sem ser cancelamento do chamador).</summary>
    private static async Task<string?> ReceberAsync(IConexaoWebSocket conexao, Uri uri, CancellationToken prazo, CancellationToken chamador)
    {
        try
        {
            return await conexao.ReceberTextoAsync(prazo).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!chamador.IsCancellationRequested)
        {
            return null;
        }
        catch (WebSocketException ex)
        {
            throw new ServicoIndisponivelException(Nome, uri, ex);
        }
        catch (JsonException ex)
        {
            throw new TranscritorException($"Resposta inválida do {Nome}.", ex);
        }
    }

    /// <summary>Envia o áudio e, ao terminar, o frame vazio; a partir daí o recebimento tem prazo para acabar.</summary>
    private async Task EnviarAsync(
        IConexaoWebSocket conexao,
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        CancellationTokenSource recebimento,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bloco in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!bloco.IsEmpty)
                {
                    await conexao.EnviarBinarioAsync(bloco, cancellationToken).ConfigureAwait(false);
                }
            }

            await conexao.EnviarBinarioAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // O servidor fechou; o recebimento descobre e encerra.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha na fonte de áudio: encerra o recebimento já, e o erro é relançado por TranscreverAsync.
            await recebimento.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!recebimento.IsCancellationRequested)
            {
                recebimento.CancelAfter(_opcoes.TimeoutFinalizacao);
            }
        }
    }

    /// <summary>Espera a tarefa terminar sem lançar; o erro, se houver, é relançado depois.</summary>
    private static Task<Task> Observar(Task tarefa) => Task.WhenAny(tarefa);
}
