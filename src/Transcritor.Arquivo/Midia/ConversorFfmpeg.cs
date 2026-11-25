using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Core.Processos;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.Arquivo.Midia;

/// <summary>
/// Usa ffprobe para ler a mídia e ffmpeg para gerar WAV PCM 16 kHz mono. Com <see cref="OpcoesArquivo.UsarCanalCentral"/>,
/// faixa com canal central (5.1, 7.1…) vira só o canal central, onde fica o diálogo; as outras são misturadas.
/// </summary>
public sealed class ConversorFfmpeg(IExecutorProcesso executor, IOptions<OpcoesArquivo> opcoes) : IConversorMidia, IServicoVerificavel
{
    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public string NomeServico => "ffmpeg";

    public async Task<InfoMidia> ObterInfoAsync(string caminhoArquivo, CancellationToken cancellationToken)
    {
        string[] argumentos = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", caminhoArquivo];
        var resultado = await executor.ExecutarAsync(_opcoes.CaminhoFfprobe, argumentos, null, cancellationToken).ConfigureAwait(false);
        if (resultado.CodigoSaida != 0)
        {
            throw new FerramentaExternaException($"O ffprobe não conseguiu ler '{caminhoArquivo}': {Resumir(resultado.Erro)}");
        }

        return InterpretarFfprobe(resultado.Saida);
    }

    public Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, Action<double>? aoAvancar, CancellationToken cancellationToken) =>
        ConverterParaWavAsync(caminhoArquivo, caminhoWav, 0, aoAvancar, cancellationToken);

    public Task ConverterParaWavAsync(
        string caminhoArquivo, string caminhoWav, int faixa, Action<double>? aoAvancar, CancellationToken cancellationToken) =>
        ConverterParaWavAsync(caminhoArquivo, caminhoWav, faixa, 16000, aoAvancar, cancellationToken);

    public async Task ConverterParaWavAsync(
        string caminhoArquivo, string caminhoWav, int faixa, int taxa, Action<double>? aoAvancar, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(faixa);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(taxa);
        var faixaTexto = faixa.ToString(CultureInfo.InvariantCulture);

        // O filtro do canal central não reclama de faixa sem esse canal (estéreo sai mudo): só pelo layout conhecido.
        var central = _opcoes.UsarCanalCentral && TemCanalCentral(await LayoutAsync(caminhoArquivo, faixaTexto, cancellationToken).ConfigureAwait(false));
        string[] mono = central ? ["-af", "pan=mono|c0=FC"] : ["-ac", "1"];
        string[] argumentos =
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", caminhoArquivo,
            "-map", $"0:a:{faixaTexto}", "-vn",
            .. mono,
            "-ar", taxa.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "wav",
            "-progress", "pipe:1", "-nostats",
            caminhoWav,
        ];

        void AoLerLinha(string linha)
        {
            if (aoAvancar is not null && TentarLerTempoProgresso(linha, out var segundos))
            {
                aoAvancar(segundos);
            }
        }

        var resultado = await executor.ExecutarAsync(_opcoes.CaminhoFfmpeg, argumentos, AoLerLinha, cancellationToken).ConfigureAwait(false);
        if (resultado.CodigoSaida != 0)
        {
            throw new FerramentaExternaException($"O ffmpeg falhou ao converter '{caminhoArquivo}': {Resumir(resultado.Erro)}");
        }
    }

    internal static bool TemCanalCentral(string? layout) => CanaisAudio.TemCanalCentral(layout);

    /// <summary>Layout de canais da faixa ("5.1", "stereo"…); nulo se o ffprobe não diz.</summary>
    private async Task<string?> LayoutAsync(string caminhoArquivo, string faixa, CancellationToken cancellationToken)
    {
        string[] argumentos = ["-v", "error", "-select_streams", $"a:{faixa}", "-show_entries", "stream=channel_layout", "-of", "csv=p=0", caminhoArquivo];
        var resultado = await executor.ExecutarAsync(_opcoes.CaminhoFfprobe, argumentos, null, cancellationToken).ConfigureAwait(false);
        var layout = resultado.CodigoSaida == 0 ? resultado.Saida.Trim() : string.Empty;
        return layout.Length == 0 ? null : layout;
    }

    public async Task VerificarAsync(CancellationToken cancellationToken)
    {
        foreach (var programa in new[] { _opcoes.CaminhoFfmpeg, _opcoes.CaminhoFfprobe })
        {
            var resultado = await executor.ExecutarAsync(programa, ["-version"], null, cancellationToken).ConfigureAwait(false);
            if (resultado.CodigoSaida != 0)
            {
                throw new FerramentaExternaException($"'{programa} -version' terminou com código {resultado.CodigoSaida}.");
            }
        }
    }

    internal static InfoMidia InterpretarFfprobe(string json)
    {
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;

        var duracao = 0d;
        var formato = string.Empty;
        if (raiz.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String)
            {
                _ = double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duracao);
            }

            if (format.TryGetProperty("format_name", out var f))
            {
                formato = f.GetString() ?? string.Empty;
            }
        }

        var faixasAudio = 0;
        var idiomas = new List<string>();
        var layouts = new List<string>();
        if (raiz.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var tipo) || tipo.GetString() != "audio")
                {
                    continue;
                }

                // A primeira faixa de áudio é a convertida; a duração dela vale mais que a do contêiner,
                // que num vídeo pode incluir imagem sem som.
                if (faixasAudio == 0
                    && stream.TryGetProperty("duration", out var da)
                    && da.ValueKind == JsonValueKind.String
                    && double.TryParse(da.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duracaoAudio)
                    && duracaoAudio > 0)
                {
                    duracao = duracaoAudio;
                }

                var idioma = stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var l)
                    && l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString()) && l.GetString() != "und"
                    ? Idiomas.Normalizar(l.GetString()!)
                    : string.Empty;
                idiomas.Add(idioma);
                layouts.Add(stream.TryGetProperty("channel_layout", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString() ?? "" : "");
                faixasAudio++;
            }
        }

        return new InfoMidia(duracao, formato, faixasAudio, idiomas, layouts);
    }

    /// <summary>Lê "out_time_us=..." da saída de -progress.</summary>
    internal static bool TentarLerTempoProgresso(string linha, out double segundos)
    {
        segundos = 0;
        const string prefixo = "out_time_us=";
        if (!linha.StartsWith(prefixo, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(linha.AsSpan(prefixo.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microssegundos) || microssegundos < 0)
        {
            return false;
        }

        segundos = microssegundos / 1_000_000d;
        return true;
    }

    private static string Resumir(string texto)
    {
        var t = texto.Trim();
        return t.Length <= 500 ? t : "..." + t[^500..];
    }
}
