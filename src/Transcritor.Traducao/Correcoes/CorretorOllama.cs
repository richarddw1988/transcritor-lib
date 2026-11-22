using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao.Correcoes;

/// <summary>
/// Correção de palavras ouvidas errado com um modelo de instrução do Ollama, travada pela pronúncia: o modelo
/// devolve a frase corrigida e só valem as trocas de uma palavra por outra que soa parecido (chave de pronúncia
/// igual ou a uma letra). Assim o modelo não reescreve a frase nem troca o sentido ("mau humor" → "bom humor").
/// Com <see cref="OpcoesTraducao.ConfirmarCorrecaoComModelo"/>, cada troca que passa ainda vai a uma segunda chamada que compara a frase ouvida com a trocada.
/// </summary>
internal sealed class CorretorOllama(ClienteOllama ollama, IOptions<OpcoesTraducao> opcoes, Dicionario.FonteDicionario fonte)
    : ICorretorTranscricao, IServicoVerificavel, IDescarregavel
{
    /// <summary>Mudar o prompt ou a trava exige subir a versão (entra na chave do cache).</summary>
    public const int Versao = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonElement Esquema = JsonDocument.Parse("""
        {"type":"object","properties":{"corrigida":{"type":"string"}},"required":["corrigida"]}
        """).RootElement.Clone();

    private static readonly JsonElement EsquemaVerificacao = JsonDocument.Parse("""
        {"type":"object","properties":{"escolha":{"type":"integer","enum":[1,2]}},"required":["escolha"]}
        """).RootElement.Clone();

    private readonly OpcoesTraducao _opcoes = opcoes.Value;

    public string Identificador => $"ollama:{_opcoes.ModeloCorrecao}:r{Versao}{(_opcoes.ConfirmarCorrecaoComModelo ? "-conf" : string.Empty)}";

    public bool Ativo => _opcoes.CorrigirTranscricao && !string.IsNullOrWhiteSpace(_opcoes.ModeloCorrecao);

    public string NomeServico => $"{ClienteOllama.Nome} (correção da transcrição)";

    public async Task<IReadOnlyList<Correcao>> CorrigirAsync(PedidoCorrecao pedido, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var podeTrocar = Enumerable.Range(0, pedido.Palavras.Count).Select(i => PodeTrocar(pedido, i)).ToArray();
        if (!podeTrocar.Any(p => p))
        {
            // Nenhuma palavra duvidosa: a frase nem vai ao modelo.
            return [];
        }

        var frase = string.Join(' ', pedido.Palavras);
        var resposta = await ollama.ConversarAsync(
            _opcoes.ModeloCorrecao,
            [new MensagemChat("system", Instrucao(pedido.Idioma)), new MensagemChat("user", Pedido(pedido, frase, Parecidas(pedido, podeTrocar)))],
            Esquema,
            maxTokens: 64 + (frase.Length * 2),
            cancellationToken).ConfigureAwait(false);

        string? corrigida;
        try
        {
            corrigida = JsonSerializer.Deserialize<RespostaCorrecao>(resposta, Json)?.Corrigida;
        }
        catch (JsonException)
        {
            // Resposta cortada ou fora do esquema: a frase fica como foi ouvida.
            return [];
        }

        if (string.IsNullOrWhiteSpace(corrigida))
        {
            return [];
        }

        // Uma troca por vez, sobre a frase com as já aceitas: em "quiet quiet" só uma das duas pode virar "quite".
        var aceitas = new List<Correcao>();
        var atual = pedido.Palavras.ToArray();
        foreach (var troca in Trocas(pedido.Palavras, corrigida, pedido.Idioma, i => podeTrocar[i], fonte.Dicionario))
        {
            if (!_opcoes.ConfirmarCorrecaoComModelo || await ConfirmarAsync(pedido, atual, troca, cancellationToken).ConfigureAwait(false))
            {
                aceitas.Add(troca);
                atual[troca.Indice] = troca.Corrigida;
            }
        }

        return aceitas;
    }

    /// <summary>
    /// Segunda opinião sobre uma troca: mostra a frase atual (1) e com só esta palavra trocada (2) e pergunta
    /// qual a pessoa disse. A atual é o padrão; a troca só vale se a atual não fizer sentido e a trocada fizer.
    /// </summary>
    private async Task<bool> ConfirmarAsync(PedidoCorrecao pedido, string[] atual, Correcao troca, CancellationToken cancellationToken)
    {
        var trocada = atual.ToArray();
        trocada[troca.Indice] = troca.Corrigida;
        var sistema =
            "A speech recognizer wrote option 1. Option 2 is the same sentence with one word replaced by a similar-sounding word. " +
            "Decide which one the speaker actually said, using the neighbouring sentences as context. Option 1 is the default: " +
            "rare, old-fashioned, poetic or unusual words are usually right. Answer 2 ONLY if option 1 clearly does not make sense " +
            "there and option 2 clearly does. Reply only with JSON: {\"escolha\": 1} or {\"escolha\": 2}.";
        var usuario = JsonSerializer.Serialize(
            new
            {
                antes = pedido.Anteriores,
                opcao1 = string.Join(' ', atual),
                opcao2 = string.Join(' ', trocada),
                palavra = $"{troca.Ouvida} -> {troca.Corrigida}",
                depois = pedido.Seguintes,
            },
            Json);
        var resposta = await ollama.ConversarAsync(
            _opcoes.ModeloCorrecao,
            [new MensagemChat("system", sistema), new MensagemChat("user", usuario)],
            EsquemaVerificacao,
            maxTokens: 16,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<RespostaVerificacao>(resposta, Json)?.Escolha == 2;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Palavra de 4+ letras com confiança abaixo do limite (ou sem confiança informada).</summary>
    private bool PodeTrocar(PedidoCorrecao pedido, int indice) =>
        Letras(pedido.Palavras[indice]).Length >= MinimoLetras
        && (pedido.Confiancas is not { } c || indice >= c.Count || c[indice] is not { } v || v < _opcoes.LimiteConfiancaCorrecao);

    public Task VerificarAsync(CancellationToken cancellationToken) =>
        Ativo ? ollama.VerificarAsync([_opcoes.ModeloCorrecao], cancellationToken) : Task.CompletedTask;

    public Task DescarregarAsync(CancellationToken cancellationToken) =>
        ollama.DescarregarAsync(_opcoes.ModeloCorrecao, cancellationToken);

    private static string Instrucao(string idioma)
    {
        var nome = Idiomas.Normalizar(idioma) == Idiomas.Portugues ? "Portuguese" : "English";
        var exemplos = Idiomas.Normalizar(idioma) == Idiomas.Portugues
            ? "\"assento\" written for \"acento\", \"coser\" for \"cozer\""
            : "\"right\" written for \"write\", \"flower\" for \"flour\"";
        return
            $"You fix speech recognition errors in {nome}. The user sends JSON with a sentence (\"frase\") transcribed from speech, " +
            "neighbouring sentences for context, and for each word a list of dictionary words that sound similar (\"parecidas\"). Some words may have been misheard: a different word that sounds the same or " +
            $"almost the same was written instead (e.g. {exemplos}). Replace ONLY such misheard words, so the sentence makes sense; " +
            "prefer a word from \"parecidas\" or another word that sounds alike. " +
            "Keep every other word, the word order, the grammar and the style exactly as they are; never rephrase, never fix grammar, " +
            "never add or remove words. If nothing was misheard, return the sentence unchanged. Reply only with JSON: " +
            "{\"corrigida\": \"<the sentence>\"}.";
    }

    private static string Pedido(PedidoCorrecao pedido, string frase, Dictionary<string, List<string>> parecidas) =>
        JsonSerializer.Serialize(new { antes = pedido.Anteriores, frase, depois = pedido.Seguintes, parecidas }, Json);

    /// <summary>Para cada palavra que pode ser trocada, as do dicionário que soam parecido (vazio sem dicionário).</summary>
    private Dictionary<string, List<string>> Parecidas(PedidoCorrecao pedido, bool[] podeTrocar)
    {
        var resultado = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (fonte.Dicionario is not { } dic)
        {
            return resultado;
        }

        foreach (var palavra in pedido.Palavras.Select(Nucleo).Where((_, i) => podeTrocar[i]))
        {
            if (!resultado.ContainsKey(palavra))
            {
                resultado[palavra] = dic.ProcurarParecidas(palavra, pedido.Idioma, MaximoParecidas).Select(p => p.Termo).ToList();
            }
        }

        return resultado;
    }

    private const int MaximoParecidas = 10;

    private const int MinimoLetras = 4;

    /// <summary>
    /// Alinha a frase do modelo com as palavras ouvidas e devolve as trocas de uma palavra por outra que soa parecido.
    /// Palavras a mais ou a menos na resposta são ignoradas.
    /// </summary>
    internal static List<Correcao> Trocas(
        IReadOnlyList<string> ouvidas, string corrigida, string idioma, Func<int, bool>? podeTrocar = null, DicionarioSqlite? dicionario = null)
    {
        var novas = corrigida.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var a = ouvidas.Select(Nucleo).ToArray();
        var b = novas.Select(Nucleo).ToArray();
        var custo = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            custo[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            custo[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var troca = custo[i - 1, j - 1] + (Igual(a[i - 1], b[j - 1]) ? 0 : 1);
                custo[i, j] = Math.Min(troca, Math.Min(custo[i - 1, j] + 1, custo[i, j - 1] + 1));
            }
        }

        var trocas = new List<Correcao>();
        int x = a.Length, y = b.Length;
        while (x > 0 && y > 0)
        {
            var igual = Igual(a[x - 1], b[y - 1]);
            if (custo[x, y] == custo[x - 1, y - 1] + (igual ? 0 : 1))
            {
                if (!igual && (podeTrocar?.Invoke(x - 1) ?? true) && SoaParecido(a[x - 1], b[y - 1], idioma)
                    && !MesmaPalavra(a[x - 1], b[y - 1], idioma, dicionario))
                {
                    trocas.Add(new Correcao(x - 1, ouvidas[x - 1], ComPontuacao(ouvidas[x - 1], b[y - 1])));
                }

                x--;
                y--;
            }
            else if (custo[x, y] == custo[x - 1, y] + 1)
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        trocas.Reverse();
        return trocas;
    }

    /// <summary>
    /// Flexão ou parte da mesma palavra ("take" → "taken", "fingers" → "finger", "slightly" → "lightly", "know" → "knew"
    /// pela forma base do dicionário): o reconhecimento quase nunca erra assim, e o modelo troca para "corrigir" gramática.
    /// </summary>
    internal static bool MesmaPalavra(string ouvida, string nova, string idioma, DicionarioSqlite? dicionario)
    {
        var a = Letras(ouvida);
        var b = Letras(nova);
        if (a == b)
        {
            return false; // só apóstrofo: tratado em SoaParecido
        }

        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
        {
            return true; // terminação a mais ou a menos: flexão
        }

        if ((a.EndsWith(b, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal))
            && !ChaveFonetica.Calcular(a, idioma).Intersect(ChaveFonetica.Calcular(b, idioma)).Any())
        {
            return true; // começo a mais que se pronuncia ("slightly" → "lightly"); "night" → "knight" soa igual e passa
        }

        if (dicionario is null)
        {
            return false;
        }

        var formas = new HashSet<string>(dicionario.Lemas(ouvida, idioma), StringComparer.OrdinalIgnoreCase) { DicionarioSqlite.Chave(ouvida) };
        return dicionario.Lemas(nova, idioma).Append(DicionarioSqlite.Chave(nova)).Any(formas.Contains);
    }

    /// <summary>Mesma palavra, ignorando maiúsculas.</summary>
    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Troca aceitável: as duas têm 4+ letras, a nova é uma palavra solta (sem número nem apóstrofo novo) e as duas têm chaves de
    /// pronúncia iguais ou a uma letra (iguais, se as chaves têm até 2 letras); ou só mudam apóstrofo ("aunt's" → "aunts").
    /// </summary>
    internal static bool SoaParecido(string ouvida, string nova, string idioma)
    {
        if (nova.Length == 0 || !nova.All(c => char.IsLetter(c) || c is '\'' or '-'))
        {
            return false;
        }

        if (Letras(ouvida) == Letras(nova))
        {
            return Letras(ouvida).Length >= MinimoLetras;
        }

        if (Letras(ouvida).Length < MinimoLetras || Letras(nova).Length < MinimoLetras || (nova.Contains('\'') && !ouvida.Contains('\'')))
        {
            // Palavra curta ("was", "mau", "the") quase sempre é de função; trocá-la vira correção de gramática.
            // Apóstrofo novo ("whispered" → "whisper'd") é grafia antiga, não erro de reconhecimento.
            return false;
        }

        var ka = ChaveFonetica.Calcular(ouvida, idioma);
        var kb = ChaveFonetica.Calcular(nova, idioma);
        // Chaves curtas ("NT" de "knight", "KT" de "cat") a uma letra já são palavras bem diferentes: precisam ser iguais.
        return ka.Any(p => kb.Any(q => ChaveFonetica.Distancia(p, q) <= (Math.Max(p.Length, q.Length) >= 3 ? 1 : 0)));
    }

    private static string Letras(string palavra) =>
        new(palavra.ToLowerInvariant().Where(char.IsLetter).ToArray());

    /// <summary>A palavra sem a pontuação em volta ("beach," → "beach").</summary>
    private static string Nucleo(string palavra) =>
        palavra.Replace('’', '\'').Trim('.', ',', '!', '?', ';', ':', '"', '“', '”', '(', ')', '…', '\'', '¿', '¡');

    /// <summary>Põe a palavra nova no lugar do núcleo da ouvida, mantendo pontuação e maiúscula inicial.</summary>
    private static string ComPontuacao(string ouvida, string nova)
    {
        var nucleo = Nucleo(ouvida);
        var inicio = ouvida.Replace('’', '\'').IndexOf(nucleo, StringComparison.Ordinal);
        if (nucleo.Length == 0 || inicio < 0)
        {
            return nova;
        }

        if (char.IsUpper(nucleo[0]) && nova.Length > 0)
        {
            nova = char.ToUpperInvariant(nova[0]) + nova[1..];
        }

        return string.Concat(ouvida.AsSpan(0, inicio), nova, ouvida.AsSpan(inicio + nucleo.Length));
    }

    private sealed record RespostaCorrecao(string? Corrigida);

    private sealed record RespostaVerificacao(int? Escolha);
}
