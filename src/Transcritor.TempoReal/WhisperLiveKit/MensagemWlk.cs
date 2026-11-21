using System.Globalization;
using System.Text.Json;

namespace Transcritor.TempoReal.WhisperLiveKit;

/// <summary>Linha do WhisperLiveKit. Speaker -2 = silêncio.</summary>
internal sealed record LinhaWlk(int Speaker, string? Texto, double Inicio, double Fim, string? Idioma)
{
    public bool EhSilencio => Speaker == -2 || string.IsNullOrWhiteSpace(Texto);
}

/// <summary>
/// Mensagem do servidor (modo "full", v0.2.27): "config" ao conectar, atualizações sem campo type
/// (estado completo: lines + buffer_transcription), "error" e "ready_to_stop".
/// </summary>
internal sealed record MensagemWlk(
    string? Tipo,
    string? Status,
    string? Erro,
    bool? UsaPcm,
    IReadOnlyList<LinhaWlk> Linhas,
    string BufferTranscricao)
{
    public bool EhErro => Tipo == "error" || Status == "error";

    public static MensagemWlk Interpretar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var linhas = new List<LinhaWlk>();
        if (r.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in ls.EnumerateArray())
            {
                linhas.Add(new LinhaWlk(
                    l.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 1,
                    Texto(l, "text"),
                    Tempo(l, "start"),
                    Tempo(l, "end"),
                    Texto(l, "detected_language")));
            }
        }

        bool? usaPcm = r.TryGetProperty("useAudioWorklet", out var ua) && ua.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? ua.GetBoolean()
            : null;

        return new MensagemWlk(
            Texto(r, "type"),
            Texto(r, "status"),
            Texto(r, "error"),
            usaPcm,
            linhas,
            Texto(r, "buffer_transcription") ?? string.Empty);
    }

    private static string? Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"H:MM:SS.cc" (formato do servidor) ou número de segundos.</summary>
    internal static double Tempo(JsonElement e, string nome)
    {
        if (!e.TryGetProperty(nome, out var v))
        {
            return 0;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }

        return v.ValueKind == JsonValueKind.String ? LerTempo(v.GetString()) : 0;
    }

    internal static double LerTempo(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return 0;
        }

        var partes = texto.Split(':');
        double total = 0;
        foreach (var parte in partes)
        {
            if (!double.TryParse(parte, NumberStyles.Float, CultureInfo.InvariantCulture, out var valor))
            {
                return 0;
            }

            total = (total * 60) + valor;
        }

        return total;
    }
}
