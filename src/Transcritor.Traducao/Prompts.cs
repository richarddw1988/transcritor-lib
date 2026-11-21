using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao;

/// <summary>
/// Prompts validados contra os modelos (ver docs/etapa-2.md).
/// Mudar o texto de um prompt exige subir <see cref="Versao"/>, que entra na chave do cache.
/// </summary>
internal static partial class Prompts
{
    public const int Versao = 3;

    private static readonly JsonSerializerOptions JsonUsuario = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON Schema da resposta dos prompts de instrução: {"traducao": "..."}.</summary>
    public static JsonElement EsquemaTraducao { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"traducao":{"type":"string"}},"required":["traducao"]}""").RootElement.Clone();

    /// <summary>Modelos da família TranslateGemma usam o template fixo de tradução.</summary>
    public static bool EhTranslateGemma(string modelo) =>
        modelo.StartsWith("translategemma", StringComparison.OrdinalIgnoreCase);

    private static (string Nome, string Codigo) Lingua(string idioma) => Idiomas.Normalizar(idioma) switch
    {
        Idiomas.Portugues => ("Brazilian Portuguese", "pt-BR"),
        Idiomas.Ingles => ("English", "en"),
        var outro => (outro, outro),
    };

    // ---- TranslateGemma ---------------------------------------------------

    /// <summary>
    /// Template oficial do TranslateGemma. O contexto vai no próprio texto, uma frase por linha, com a
    /// frase-alvo entre &lt;t&gt;...&lt;/t&gt;; o modelo preserva as tags e assim a tradução da frase pode ser recortada.
    /// Sem contexto o modelo traduzia "banco" como "bank" mesmo numa praça; com as frases na mesma linha,
    /// às vezes envolvia o trecho inteiro na tag quando o alvo era a primeira frase.
    /// </summary>
    public static MensagemChat TranslateGemma(PedidoTraducaoFrase pedido, bool comContexto)
    {
        var (origem, codigoOrigem) = Lingua(pedido.Origem);
        var (destino, codigoDestino) = Lingua(pedido.Destino);
        var texto = comContexto && TemContexto(pedido)
            ? string.Join('\n', [.. pedido.Anteriores, $"<t>{pedido.Texto}</t>", .. pedido.Seguintes])
            : pedido.Texto;

        return new MensagemChat("user",
            $"You are a professional {origem} ({codigoOrigem}) to {destino} ({codigoDestino}) translator. " +
            $"Your goal is to accurately convey the meaning and nuances of the original {origem} text while adhering to " +
            $"{destino} grammar, vocabulary, and cultural sensitivities. Produce only the {destino} translation, without any " +
            $"additional explanations or commentary. Please translate the following {origem} text into {destino}:\n\n\n{texto}");
    }

    public static bool TemContexto(PedidoTraducaoFrase pedido) =>
        pedido.Anteriores.Count > 0 || pedido.Seguintes.Count > 0;

    /// <summary>
    /// Recorta o trecho entre &lt;t&gt; e &lt;/t&gt;. Nulo se o modelo não preservou as tags, ou se o recorte
    /// claramente engoliu frases vizinhas (quebra de linha ou tamanho muito maior que o original).
    /// </summary>
    public static string? RecortarAlvo(string resposta, string original)
    {
        var m = RegexAlvo().Match(resposta);
        if (!m.Success)
        {
            return null;
        }

        var recorte = m.Groups[1].Value.Trim();
        if (recorte.Length == 0 || recorte.Contains('\n', StringComparison.Ordinal) || recorte.Length > (original.Trim().Length * 2) + 30)
        {
            return null;
        }

        // Modelo que "enfeita" a frase curta com frases a mais ("Hurry!" → "Apresse-se! Rápido! Corra!").
        if (ContarFinaisDeFrase(recorte) > ContarFinaisDeFrase(original) + 1)
        {
            return null;
        }

        return recorte;
    }

    private static int ContarFinaisDeFrase(string texto) => RegexFimDeFrase().Count(texto);

    /// <summary>Teto de tokens gerados: evita que um modelo em repetição prenda a chamada até o timeout.</summary>
    public static int MaxTokens(string textoEnviado) => 64 + (textoEnviado.Length * 2);

    [GeneratedRegex(@"<t>(.*?)</t>", RegexOptions.Singleline)]
    private static partial Regex RegexAlvo();

    [GeneratedRegex(@"[.!?…]+(?=\s|$)")]
    private static partial Regex RegexFimDeFrase();

    // ---- Modelos de instrução (gemma3, qwen, llama...) --------------------

    public static IReadOnlyList<MensagemChat> FraseInstrucao(PedidoTraducaoFrase pedido)
    {
        var (origem, _) = Lingua(pedido.Origem);
        var (destino, _) = Lingua(pedido.Destino);
        var sistema =
            $"You are a {origem}-to-{destino} subtitle translator. The user sends JSON with the field \"frase\" (ONE sentence to translate) " +
            "and the fields \"antes\" and \"depois\" (neighbouring sentences, context ONLY - never translate or repeat them). " +
            $"Translate \"frase\" by meaning, as a native {destino} speaker would say it, using the context only to resolve ambiguity. " +
            "Do not add or remove information. Reply with JSON {\"traducao\": \"<translation of frase only>\"}.";
        var usuario = JsonSerializer.Serialize(new { antes = pedido.Anteriores, frase = pedido.Texto, depois = pedido.Seguintes }, JsonUsuario);
        return [new MensagemChat("system", sistema), new MensagemChat("user", usuario)];
    }

    public static IReadOnlyList<MensagemChat> Palavra(PedidoTraducaoPalavra pedido, string traducaoDaFrase)
    {
        var (origem, _) = Lingua(pedido.Origem);
        var (destino, _) = Lingua(pedido.Destino);
        var sistema =
            $"The user sends a {origem} sentence, its {destino} translation and one word taken from the {origem} sentence. " +
            $"Say what that word means in THIS sentence, in {destino}. If the word is part of an idiom or fixed expression " +
            "(e.g. phrasal verb), translate the meaning of the whole expression instead of the literal word. " +
            "Prefer the wording used in the given translation. Reply with JSON {\"traducao\": \"<one word or short expression>\"}. No explanations.";
        var usuario = JsonSerializer.Serialize(
            new { frase = pedido.Frase, traducao_da_frase = traducaoDaFrase, palavra = pedido.Palavra }, JsonUsuario);
        return [new MensagemChat("system", sistema), new MensagemChat("user", usuario)];
    }

    /// <summary>JSON Schema da escolha entre opções do dicionário: {"opcao": n, "traducao": "..."}.</summary>
    public static JsonElement EsquemaOpcao { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"opcao":{"type":"integer"},"traducao":{"type":"string"}},"required":["opcao","traducao"]}""")
        .RootElement.Clone();

    /// <summary>
    /// Palavra com os sentidos do dicionário numerados: o modelo escolhe um (formato de escolha múltipla, como nos
    /// estudos de desambiguação de sentido com LLM) e adapta a tradução à frase. 0 = nenhuma opção serve.
    /// </summary>
    public static IReadOnlyList<MensagemChat> PalavraComOpcoes(
        PedidoTraducaoPalavra pedido, string traducaoDaFrase, IReadOnlyList<Dicionario.Candidato> opcoes)
    {
        var (origem, _) = Lingua(pedido.Origem);
        var (destino, _) = Lingua(pedido.Destino);
        var sistema =
            $"The user sends a {origem} sentence, its {destino} translation, one word taken from the {origem} sentence and " +
            $"numbered dictionary options (term, {destino} translation and the sense it refers to). Pick the option whose " +
            "sense matches how the word is used in THIS sentence; options for a whole expression (e.g. phrasal verb, idiom) " +
            "containing the word are preferred when the sentence uses that expression. Options marked (in translation) " +
            "appear in the given translation and are usually the right sense. If no option fits, use 0. " +
            $"Reply with JSON {{\"opcao\": <number>, \"traducao\": \"<the chosen {destino} translation, adapted to this sentence " +
            "(e.g. conjugated) and preferring the wording of the given translation; your own translation if opcao is 0>\"}. " +
            "No explanations.";
        var linhas = opcoes.Select((c, i) =>
            $"{i + 1}. {c.Termo} ({c.Classe}): {c.Traducao}{(c.Sentido is null ? string.Empty : $" [{c.Sentido}]")}" +
            (c.NaTraducao ? " (in translation)" : string.Empty));
        var usuario = JsonSerializer.Serialize(
            new { frase = pedido.Frase, traducao_da_frase = traducaoDaFrase, palavra = pedido.Palavra }, JsonUsuario) +
            "\nopcoes:\n" + string.Join('\n', linhas);
        return [new MensagemChat("system", sistema), new MensagemChat("user", usuario)];
    }

    /// <summary>Lê {"opcao": n, "traducao": "..."}; opção nula se a resposta não for esse JSON.</summary>
    public static (int? Opcao, string Traducao) LerJsonOpcao(string resposta)
    {
        try
        {
            using var doc = JsonDocument.Parse(resposta);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object)
            {
                int? opcao = raiz.TryGetProperty("opcao", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var n)
                    ? n
                    : null;
                var traducao = raiz.TryGetProperty("traducao", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                return (opcao, traducao);
            }
        }
        catch (JsonException)
        {
        }

        return (null, resposta);
    }

    /// <summary>Lê {"traducao": "..."}; se o modelo não devolveu JSON, usa o texto cru.</summary>
    public static string LerJsonTraducao(string resposta)
    {
        try
        {
            using var doc = JsonDocument.Parse(resposta);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("traducao", out var t)
                && t.ValueKind == JsonValueKind.String)
            {
                return t.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return resposta;
    }

    /// <summary>Tira espaços, quebras de linha e aspas que o modelo pôs em volta (se o original não tinha).</summary>
    public static string Limpar(string traducao, string original)
    {
        var t = string.Join(' ', traducao.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        var o = original.Trim();
        foreach (var (abre, fecha) in new[] { ('"', '"'), ('“', '”'), ('\'', '\'') })
        {
            if (t.Length >= 2 && t[0] == abre && t[^1] == fecha && !(o.Length > 0 && o[0] == abre))
            {
                t = t[1..^1].Trim();
            }
        }

        // Frases a mais inventadas pelo modelo ("Hurry!" → "Apresse-se! Rápido! Corra!"): fica com as primeiras.
        var finaisOriginal = ContarFinaisDeFrase(o);
        var finais = RegexFimDeFrase().Matches(t);
        if (finais.Count > finaisOriginal + 1)
        {
            var ultimo = finais[Math.Max(1, finaisOriginal) - 1];
            t = t[..(ultimo.Index + ultimo.Length)];
        }

        return t;
    }
}
