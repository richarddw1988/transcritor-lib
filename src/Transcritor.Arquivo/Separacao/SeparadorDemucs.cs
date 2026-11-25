using System.Buffers.Binary;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Midia;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.Arquivo.Separacao;

/// <summary>Separa a voz da música e dos efeitos, para a transcrição ouvir só a fala (filmes).</summary>
public interface ISeparadorVoz
{
    /// <summary>Identifica o separador e o modelo; entra na chave do cache.</summary>
    string Identificador { get; }

    /// <summary>Gera um WAV 16 kHz mono só com a voz.</summary>
    /// <param name="wavEntrada">WAV PCM 16 bits (a lib manda 44,1 kHz mono).</param>
    /// <param name="wavSaida">WAV de saída, 16 kHz mono (sobrescrito).</param>
    /// <param name="aoAvancar">Recebe o percentual (0 a 100).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    Task SepararAsync(string wavEntrada, string wavSaida, Action<double>? aoAvancar, CancellationToken cancellationToken);
}

/// <summary>
/// Cliente do serviço <c>separador</c> (docker/separador: Demucs <c>htdemucs_ft</c>). O Demucs carrega o áudio inteiro
/// na memória (um filme de 2 h derrubou a VM do WSL), então o áudio vai em partes de
/// <see cref="OpcoesArquivo.SegundosPorParteSeparacao"/> e as vozes voltam emendadas. Depois da última parte o modelo
/// sai da GPU (<see cref="DescarregarAsync"/>), antes do Whisper entrar.
/// </summary>
public sealed class SeparadorDemucs(HttpClient http, IOptions<OpcoesArquivo> opcoes) : ISeparadorVoz, IServicoVerificavel, IDescarregavel
{
    internal const string Nome = "Separador de voz";

    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public string NomeServico => Nome;

    public string Identificador => "demucs-htdemucs_ft";

    private Uri Base => http.BaseAddress ?? _opcoes.SeparadorUrl;

    public async Task SepararAsync(string wavEntrada, string wavSaida, Action<double>? aoAvancar, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(wavEntrada, cancellationToken).ConfigureAwait(false);
        var (formato, _, tamanhoDados) = RecorteWav.LerChunks(bytes);
        var taxa = BinaryPrimitives.ReadInt32LittleEndian(formato.AsSpan(4));
        var alinhamento = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(12));
        var duracao = alinhamento == 0 || taxa <= 0 ? 0 : tamanhoDados / alinhamento / (double)taxa;
        var parte = _opcoes.SegundosPorParteSeparacao > 0 ? _opcoes.SegundosPorParteSeparacao : Math.Max(duracao, 1);

        var recorte = wavSaida + ".parte.wav";
        var vozes = new List<byte[]>();
        try
        {
            for (double inicio = 0; inicio < duracao; inicio += parte)
            {
                await RecorteWav.RecortarAsync(bytes, recorte, inicio, Math.Min(duracao, inicio + parte), cancellationToken).ConfigureAwait(false);
                vozes.Add(await SepararParteAsync(await File.ReadAllBytesAsync(recorte, cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false));
                aoAvancar?.Invoke(Math.Min(100, Math.Round((inicio + parte) / duracao * 100, 1)));
            }
        }
        finally
        {
            if (File.Exists(recorte))
            {
                File.Delete(recorte);
            }
        }

        await File.WriteAllBytesAsync(wavSaida, Emendar(vozes), cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SepararParteAsync(byte[] wav, CancellationToken cancellationToken)
    {
        using var conteudo = new ByteArrayContent(wav);
        conteudo.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var resposta = await EnviarAsync(
            () => http.PostAsync(new Uri("separar", UriKind.Relative), conteudo, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!resposta.IsSuccessStatusCode)
        {
            var erro = await resposta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new TranscritorException($"O {Nome} em {Base} falhou ({(int)resposta.StatusCode}): {erro.Trim()}");
        }

        return await resposta.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Junta os WAVs das partes (todos 16 kHz mono 16 bits) num só.</summary>
    internal static byte[] Emendar(IReadOnlyList<byte[]> partes)
    {
        if (partes.Count == 0)
        {
            return CabecalhoWav(0, 16000, 1, 16);
        }

        var lidos = partes.Select(RecorteWav.LerChunks).ToList();
        var formato = lidos[0].Formato;
        var canais = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(2));
        var taxa = BinaryPrimitives.ReadInt32LittleEndian(formato.AsSpan(4));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(14));
        var total = lidos.Sum(l => l.TamanhoDados);
        var saida = new byte[44 + total];
        CabecalhoWav((int)total, taxa, canais, bits).CopyTo(saida, 0);
        var pos = 44;
        for (var i = 0; i < partes.Count; i++)
        {
            Array.Copy(partes[i], lidos[i].InicioDados, saida, pos, lidos[i].TamanhoDados);
            pos += (int)lidos[i].TamanhoDados;
        }

        return saida;
    }

    private static byte[] CabecalhoWav(int tamanho, int taxa, ushort canais, ushort bits)
    {
        var alinhamento = (ushort)(canais * bits / 8);
        var c = new byte[44];
        "RIFF"u8.CopyTo(c);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(4), 36 + tamanho);
        "WAVE"u8.CopyTo(c.AsSpan(8));
        "fmt "u8.CopyTo(c.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(22), canais);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(24), taxa);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(28), taxa * alinhamento);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(32), alinhamento);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(34), bits);
        "data"u8.CopyTo(c.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(40), tamanho);
        return c;
    }

    /// <summary>Tira o modelo da GPU. Falha de rede é ignorada (o serviço pode estar fora do ar).</summary>
    public async Task DescarregarAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var resposta = await http.PostAsync(new Uri("descarregar", UriKind.Relative), null, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Confere o /health; com <see cref="OpcoesArquivo.SepararVoz"/> = Nunca não precisa do serviço e não confere.</summary>
    public async Task VerificarAsync(CancellationToken cancellationToken)
    {
        if (_opcoes.SepararVoz == ModoSeparacaoVoz.Nunca)
        {
            return;
        }

        using var saude = await EnviarAsync(() => http.GetAsync(new Uri("health", UriKind.Relative), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (!saude.IsSuccessStatusCode)
        {
            throw new ServicoIndisponivelException($"O {Nome} em {Base} respondeu {(int)saude.StatusCode} no /health.");
        }
    }

    private async Task<HttpResponseMessage> EnviarAsync(Func<Task<HttpResponseMessage>> enviar, CancellationToken cancellationToken)
    {
        try
        {
            return await enviar().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServicoIndisponivelException(
                $"O {Nome} não respondeu em {Base}. Suba o contêiner (docker compose up -d separador) ou desligue a separação " +
                "(SepararVoz = Nunca).",
                ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServicoIndisponivelException(
                $"O {Nome} em {Base} não respondeu dentro do tempo limite ({http.Timeout}); aumente TimeoutSeparacao.", ex);
        }
    }
}
