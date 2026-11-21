using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo;
using Transcritor.Arquivo.Midia;
using Transcritor.Arquivo.Processos;
using Transcritor.Core.Contratos;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<(HttpRequestMessage Requisicao, string Corpo)> Recebidas { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Recebidas.Add((request, corpo));
        return responder(request, corpo);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeExecutorProcesso(Func<string, IReadOnlyList<string>, Action<string>?, ResultadoProcesso> executar) : IExecutorProcesso
{
    public List<(string Executavel, IReadOnlyList<string> Argumentos)> Chamadas { get; } = [];

    public Task<ResultadoProcesso> ExecutarAsync(string executavel, IReadOnlyList<string> argumentos, Action<string>? aoLerLinha, CancellationToken cancellationToken)
    {
        Chamadas.Add((executavel, argumentos));
        return Task.FromResult(executar(executavel, argumentos, aoLerLinha));
    }
}

internal sealed class FakeConversor(InfoMidia info) : IConversorMidia
{
    public int ChamadasInfo { get; private set; }
    public int ChamadasConversao { get; private set; }
    public string? UltimoWav { get; private set; }

    public Task<InfoMidia> ObterInfoAsync(string caminhoArquivo, CancellationToken cancellationToken)
    {
        ChamadasInfo++;
        return Task.FromResult(info);
    }

    public async Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, Action<double>? aoAvancar, CancellationToken cancellationToken)
    {
        ChamadasConversao++;
        UltimoWav = caminhoWav;
        await File.WriteAllBytesAsync(caminhoWav, [1, 2, 3], cancellationToken);
        aoAvancar?.Invoke(info.Duracao / 2);
        aoAvancar?.Invoke(info.Duracao);
    }
}

internal sealed class FakeMotor(TranscricaoBruta resposta, string identificador = "fake:modelo") : IMotorTranscricao
{
    public int Chamadas { get; private set; }
    public string? UltimoIdioma { get; private set; }
    public bool WavExistiaNaChamada { get; private set; }

    public string Identificador => identificador;

    public Task<TranscricaoBruta> TranscreverAsync(string caminhoWav, string? idioma, CancellationToken cancellationToken)
    {
        Chamadas++;
        UltimoIdioma = idioma;
        WavExistiaNaChamada = File.Exists(caminhoWav);
        return Task.FromResult(resposta);
    }
}

/// <summary>Tradução falsa: "[origem>destino] texto".</summary>
internal sealed class FakeTradutor(string identificador = "fake-tradutor") : ITradutor
{
    public int ChamadasLote { get; private set; }
    public (string Origem, string Destino)? UltimosIdiomas { get; private set; }

    public string Identificador => identificador;

    public Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken) =>
        Task.FromResult($"[{pedido.Origem}>{pedido.Destino}] {pedido.Texto}");

    public Task<IReadOnlyList<string>> TraduzirFrasesAsync(
        IReadOnlyList<string> frases, string origem, string destino, IProgress<double>? progresso, CancellationToken cancellationToken)
    {
        ChamadasLote++;
        UltimosIdiomas = (origem, destino);
        progresso?.Report(100);
        return Task.FromResult<IReadOnlyList<string>>(frases.Select(f => $"[{origem}>{destino}] {f}").ToList());
    }

    public Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken) =>
        Task.FromResult($"[{pedido.Origem}>{pedido.Destino}] {pedido.Palavra}");
}

internal sealed class ProgressoSincrono : IProgress<ProgressoProcessamento>
{
    public List<ProgressoProcessamento> Recebidos { get; } = [];

    public void Report(ProgressoProcessamento value) => Recebidos.Add(value);
}

/// <summary>Pasta temporária apagada no Dispose.</summary>
internal sealed class PastaTemporaria : IDisposable
{
    public PastaTemporaria()
    {
        Caminho = Path.Combine(Path.GetTempPath(), "transcritor-testes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Caminho);
    }

    public string Caminho { get; }

    public string Arquivo(string nome, string conteudo)
    {
        var caminho = Path.Combine(Caminho, nome);
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Caminho, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Opcoes
{
    public static IOptions<OpcoesArquivo> Arquivo(Action<OpcoesArquivo>? ajustar = null)
    {
        var o = new OpcoesArquivo();
        ajustar?.Invoke(o);
        return Options.Create(o);
    }
}
