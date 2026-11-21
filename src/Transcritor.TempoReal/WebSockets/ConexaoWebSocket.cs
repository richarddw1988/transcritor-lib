using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Transcritor.TempoReal.WebSockets;

/// <summary>Conexão WebSocket mínima. Existe para trocar por um fake nos testes.</summary>
public interface IConexaoWebSocket : IAsyncDisposable
{
    Task ConectarAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Envia um frame binário. Um frame vazio sinaliza fim do áudio para o WhisperLiveKit.</summary>
    Task EnviarBinarioAsync(ReadOnlyMemory<byte> dados, CancellationToken cancellationToken);

    /// <summary>Próxima mensagem de texto; nulo quando o servidor fechou a conexão.</summary>
    Task<string?> ReceberTextoAsync(CancellationToken cancellationToken);
}

public interface IFabricaConexaoWebSocket
{
    IConexaoWebSocket Criar();
}

/// <summary>Implementação com <see cref="ClientWebSocket"/>.</summary>
public sealed class ConexaoClientWebSocket : IConexaoWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConectarAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public async Task EnviarBinarioAsync(ReadOnlyMemory<byte> dados, CancellationToken cancellationToken) =>
        await _socket.SendAsync(dados, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);

    public async Task<string?> ReceberTextoAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var mensagem = new MemoryStream();
            while (true)
            {
                if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
                {
                    return null;
                }

                var resultado = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (resultado.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                mensagem.Write(buffer, 0, resultado.Count);
                if (resultado.EndOfMessage)
                {
                    if (resultado.MessageType != WebSocketMessageType.Text)
                    {
                        mensagem.SetLength(0);
                        continue;
                    }

                    return Encoding.UTF8.GetString(mensagem.GetBuffer(), 0, (int)mensagem.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Fechar é cortesia; a conexão é descartada de qualquer jeito.
            }
        }

        _socket.Dispose();
    }
}

public sealed class FabricaClientWebSocket : IFabricaConexaoWebSocket
{
    public IConexaoWebSocket Criar() => new ConexaoClientWebSocket();
}
