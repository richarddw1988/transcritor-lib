using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao.Analise;

/// <summary>
/// Unidades de sentido e "o que quis dizer" com um modelo de instrução do Ollama (JSON Schema).
/// Prompt validado com gemma3 em frases de um audiolivro e expressões idiomáticas (docs/etapa-4.md).
/// </summary>
internal sealed class AnalisadorOllama(ClienteOllama ollama, IOptions<OpcoesTraducao> opcoes) : IAnalisadorSentido, IServicoVerificavel, IDescarregavel
{
    /// <summary>Mudar o prompt exige subir a versão (entra na chave do cache).</summary>
    public const int Versao = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonElement Esquema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "sentido":{"type":"string"},
          "blocos":{"type":"array","items":{"type":"object","properties":{
            "origem":{"type":"string"},"traducao":{"type":"string"},
            "tipo":{"type":"string","enum":["expressao","phrasal","colocacao","nome","literal"]},
            "nota":{"type":"string"}},"required":["origem","traducao","tipo","nota"]}}},
         "required":["sentido","blocos"]}
        """).RootElement.Clone();

    private readonly OpcoesTraducao _opcoes = opcoes.Value;

    public string Identificador => $"ollama:{_opcoes.ModeloAnalise}:a{Versao}";

    public bool Ativo => _opcoes.AnalisarSentido && !string.IsNullOrWhiteSpace(_opcoes.ModeloAnalise);

    public string NomeServico => $"{ClienteOllama.Nome} (análise de sentido)";

    public async Task<AnaliseFrase> AnalisarAsync(PedidoAnaliseFrase pedido, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (pedido.Palavras.Count == 0 || string.IsNullOrWhiteSpace(pedido.Traducao))
        {
            return new AnaliseFrase(null, []);
        }

        var frase = string.Join(' ', pedido.Palavras);
        var origem = NomeIdioma(pedido.Origem);
        var destino = NomeIdioma(pedido.Destino);
        var sistema =
            $"You help a {destino} speaker understand a {origem} sentence. The user sends JSON with the sentence (\"frase\"), " +
            $"its {destino} translation (\"traducao\") and neighbouring sentences for context.\n" +
            "1. Split \"frase\" into meaning units, in order, covering every word exactly once: idioms, phrasal verbs, collocations, " +
            "proper names, or plain chunks of 1-5 words that translate together. Never split an idiom or phrasal verb.\n" +
            "2. For each unit: \"origem\" = the exact words copied from \"frase\"; \"traducao\" = the exact part of \"traducao\" that " +
            "renders it, copied verbatim; \"tipo\" = expressao | phrasal | colocacao | nome | literal; \"nota\" = a short note in " +
            $"{destino} ONLY when the literal meaning would mislead (false friend, idiom, archaic or formal word), else \"\". " +
            "Leave \"nota\" empty for names, dates, plain words and anything already clear from \"traducao\"; never write that there is " +
            "no trap, and never just repeat the translation.\n" +
            $"3. \"sentido\": one or two sentences in {destino} saying what the speaker meant (intention, tone), using the context.\n" +
            "Reply only with JSON.";
        var usuario = JsonSerializer.Serialize(
            new { antes = pedido.Anteriores, frase, traducao = pedido.Traducao, depois = pedido.Seguintes }, Json);

        var resposta = await ollama.ConversarAsync(
            _opcoes.ModeloAnalise,
            [new MensagemChat("system", sistema), new MensagemChat("user", usuario)],
            Esquema,
            maxTokens: 400 + (frase.Length * 6),
            cancellationToken).ConfigureAwait(false);

        RespostaAnalise? lida;
        try
        {
            lida = JsonSerializer.Deserialize<RespostaAnalise>(resposta, Json);
        }
        catch (JsonException)
        {
            // Resposta cortada ou fora do esquema: a frase fica sem análise, o resto do arquivo segue.
            return new AnaliseFrase(null, []);
        }

        var blocos = AlinhadorBlocos.Alinhar(
            pedido.Palavras,
            (lida?.Blocos ?? []).Select(b => new BlocoBruto(b.Origem ?? "", b.Traducao ?? "", b.Tipo ?? "", b.Nota ?? "")));
        var sentido = string.IsNullOrWhiteSpace(lida?.Sentido) ? null : lida.Sentido.Trim();
        return new AnaliseFrase(sentido, blocos);
    }

    public Task VerificarAsync(CancellationToken cancellationToken) =>
        Ativo ? ollama.VerificarAsync([_opcoes.ModeloAnalise], cancellationToken) : Task.CompletedTask;

    public Task DescarregarAsync(CancellationToken cancellationToken) =>
        ollama.DescarregarAsync(_opcoes.ModeloAnalise, cancellationToken);

    private static string NomeIdioma(string idioma) => Idiomas.Normalizar(idioma) switch
    {
        Idiomas.Portugues => "Brazilian Portuguese",
        Idiomas.Ingles => "English",
        var outro => outro,
    };

    private sealed record RespostaAnalise(
        [property: JsonPropertyName("sentido")] string? Sentido,
        [property: JsonPropertyName("blocos")] IReadOnlyList<BlocoResposta>? Blocos);

    private sealed record BlocoResposta(
        [property: JsonPropertyName("origem")] string? Origem,
        [property: JsonPropertyName("traducao")] string? Traducao,
        [property: JsonPropertyName("tipo")] string? Tipo,
        [property: JsonPropertyName("nota")] string? Nota);
}
