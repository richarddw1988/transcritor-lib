using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Transcritor.Core.Modelos;

namespace Transcritor.Core;

/// <summary>Lê e grava o JSON de transcrição no formato do contrato.</summary>
public static class JsonTranscricao
{
    public static JsonSerializerOptions Opcoes { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serializar(ResultadoTranscricao resultado) =>
        JsonSerializer.Serialize(resultado, Opcoes);

    public static ResultadoTranscricao Desserializar(string json) =>
        JsonSerializer.Deserialize<ResultadoTranscricao>(json, Opcoes)
        ?? throw new JsonException("JSON de transcrição vazio.");

    public static async Task GravarAsync(ResultadoTranscricao resultado, string caminho, CancellationToken cancellationToken = default)
    {
        var pasta = Path.GetDirectoryName(Path.GetFullPath(caminho));
        if (!string.IsNullOrEmpty(pasta))
        {
            Directory.CreateDirectory(pasta);
        }

        // Grava num temporário e troca, para nunca deixar um JSON pela metade.
        var temporario = caminho + ".tmp";
        await File.WriteAllTextAsync(temporario, Serializar(resultado), cancellationToken).ConfigureAwait(false);
        File.Move(temporario, caminho, overwrite: true);
    }

    public static async Task<ResultadoTranscricao> LerAsync(string caminho, CancellationToken cancellationToken = default) =>
        Desserializar(await File.ReadAllTextAsync(caminho, cancellationToken).ConfigureAwait(false));
}
