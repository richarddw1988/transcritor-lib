using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.TempoReal;

/// <summary>
/// Liga fonte de áudio → motor de tempo real → tradução. Os eventos do motor passam direto; cada Confirmado
/// que precisa de tradução é traduzido em segundo plano (em ordem, com os trechos anteriores como contexto) e
/// reemitido com o mesmo Id e a tradução preenchida. A tradução nunca atrasa os eventos de reconhecimento.
/// </summary>
public sealed class TranscritorTempoReal(
    IMotorTempoReal motor,
    IOptions<OpcoesTempoReal> opcoes,
    IFabricaFonteAudio? fabricaFontes = null,
    ITradutor? tradutor = null) : ITranscritorTempoReal
{
    private readonly OpcoesTempoReal _opcoes = opcoes.Value;

    public IAsyncEnumerable<EventoTempoReal> IniciarAsync(Fonte fonte, Saida saida, OpcoesSessaoTempoReal? sessao = null, CancellationToken cancellationToken = default)
    {
        var fabrica = fabricaFontes ?? throw new InvalidOperationException(
            "Nenhuma IFabricaFonteAudio registrada. No Windows, chame services.AddTranscritorCaptura().");
        return Executar(fabrica.Criar(fonte), descartarFonte: true, saida, sessao ?? new OpcoesSessaoTempoReal(), cancellationToken);
    }

    public IAsyncEnumerable<EventoTempoReal> IniciarAsync(IFonteAudio fonte, Saida saida, OpcoesSessaoTempoReal? sessao = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fonte);
        return Executar(fonte, descartarFonte: false, saida, sessao ?? new OpcoesSessaoTempoReal(), cancellationToken);
    }

    private async IAsyncEnumerable<EventoTempoReal> Executar(
        IFonteAudio fonte,
        bool descartarFonte,
        Saida saida,
        OpcoesSessaoTempoReal sessao,
        [EnumeratorCancellation] CancellationToken parar)
    {
        // "parar" encerra só a captura; "abortar" corta tudo se a finalização passar do prazo
        // ou se quem consome parar de ler.
        using var abortar = new CancellationTokenSource();
        await using var registro = parar.Register(() =>
        {
            try
            {
                abortar.CancelAfter(_opcoes.TimeoutFinalizacao + TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
            }
        }).ConfigureAwait(false);

        var saidaEventos = Channel.CreateUnbounded<EventoTempoReal>(new UnboundedChannelOptions { SingleReader = true });
        var paraTraduzir = Channel.CreateUnbounded<EventoTempoReal>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var reconhecimento = Task.Run(() => ReconhecerAsync(fonte, sessao, saidaEventos.Writer, paraTraduzir.Writer, parar, abortar.Token), CancellationToken.None);
        var traducao = Task.Run(() => TraduzirAsync(saida, paraTraduzir.Reader, saidaEventos.Writer, abortar.Token), CancellationToken.None);
        var tudo = Task.WhenAll(reconhecimento, traducao).ContinueWith(
            t => saidaEventos.Writer.TryComplete(t.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var evento in saidaEventos.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return evento;
            }
        }
        finally
        {
            await abortar.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(tudo).ConfigureAwait(false);
            if (descartarFonte)
            {
                await fonte.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReconhecerAsync(
        IFonteAudio fonte,
        OpcoesSessaoTempoReal sessao,
        ChannelWriter<EventoTempoReal> saida,
        ChannelWriter<EventoTempoReal> paraTraduzir,
        CancellationToken parar,
        CancellationToken abortar)
    {
        try
        {
            await foreach (var evento in motor.TranscreverAsync(fonte.CapturarAsync(parar), sessao, abortar).ConfigureAwait(false))
            {
                await saida.WriteAsync(evento, abortar).ConfigureAwait(false);
                if (evento.Tipo == TipoEvento.Confirmado)
                {
                    await paraTraduzir.WriteAsync(evento, abortar).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (abortar.IsCancellationRequested)
        {
            // Prazo de finalização estourado ou consumidor saiu: termina com o que já foi entregue.
        }
        finally
        {
            paraTraduzir.TryComplete();
        }
    }

    private async Task TraduzirAsync(
        Saida saida,
        ChannelReader<EventoTempoReal> confirmados,
        ChannelWriter<EventoTempoReal> eventos,
        CancellationToken abortar)
    {
        var anteriores = new List<string>();
        try
        {
            await foreach (var evento in confirmados.ReadAllAsync(abortar).ConfigureAwait(false))
            {
                var destino = Destino(saida, evento.Idioma);
                if (destino is not null)
                {
                    var t = tradutor ?? throw new InvalidOperationException(
                        $"Saida.{saida} pede tradução, mas nenhum ITradutor foi registrado. Chame services.AddTranscritorTraducao().");
                    var contexto = anteriores.TakeLast(Math.Max(0, _opcoes.TrechosContexto)).ToList();
                    var traduzido = await t.TraduzirFraseAsync(
                        new PedidoTraducaoFrase(evento.Texto, evento.Idioma!, destino, contexto, []), abortar).ConfigureAwait(false);
                    await eventos.WriteAsync(evento with { Traducao = traduzido }, abortar).ConfigureAwait(false);
                }

                anteriores.Add(evento.Texto);
            }
        }
        catch (OperationCanceledException) when (abortar.IsCancellationRequested)
        {
        }
    }

    /// <summary>Idioma da tradução; nulo se não precisa traduzir ou se o idioma falado ainda é desconhecido.</summary>
    private static string? Destino(Saida saida, string? idiomaFalado)
    {
        if (string.IsNullOrWhiteSpace(idiomaFalado))
        {
            return null;
        }

        try
        {
            return RegraSaida.IdiomaDaTraducao(saida, idiomaFalado);
        }
        catch (TranscritorException)
        {
            return null; // Ambos com fala em outro idioma: transcreve sem traduzir.
        }
    }
}
