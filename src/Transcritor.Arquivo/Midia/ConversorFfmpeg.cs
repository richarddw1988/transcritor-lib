using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Processos;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;

namespace Transcritor.Arquivo.Midia;

/// <summary>Usa ffprobe para ler a mídia e ffmpeg para gerar WAV PCM 16 kHz mono.</summary>
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

    public async Task ConverterParaWavAsync(string caminhoArquivo, string caminhoWav, Action<double>? aoAvancar, CancellationToken cancellationToken)
    {
        string[] argumentos =
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", caminhoArquivo,
            "-map", "0:a:0", "-vn",
            "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav",
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

                faixasAudio++;
            }
        }

        return new InfoMidia(duracao, formato, faixasAudio);
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
