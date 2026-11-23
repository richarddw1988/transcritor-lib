using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Transcritor.Core;

namespace Transcritor.Traducao.Correcoes;

/// <summary>Grafia possível de uma palavra e a probabilidade de ser ela, pelo sentido da frase.</summary>
/// <param name="Grafia">Palavra (minúsculas, como no dicionário; maiúscula inicial se a ouvida tinha).</param>
/// <param name="Probabilidade">
/// Entre 0 e 1: a menor das duas estimativas do BERT (ver <c>Modelo.Nota</c>), então as grafias de uma palavra podem
/// somar menos de 1.
/// </param>
internal sealed record GrafiaProvavel(string Grafia, double Probabilidade);

/// <summary>Dá a probabilidade de cada grafia de um homófono na frase. Existe para trocar o BERT por um falso nos testes.</summary>
internal interface IEscolhedorHomofonos
{
    /// <summary>Tem modelo para o idioma.</summary>
    bool Disponivel(string idioma);

    /// <summary>
    /// Probabilidade de cada uma das <paramref name="grafias"/> na posição <paramref name="indice"/> da frase (as outras
    /// palavras ficam como estão), da mais provável para a menos; vazio se a frase for longa demais para o modelo.
    /// Com <paramref name="lacunaNasRepetidas"/>, outras ocorrências da mesma palavra viram lacuna, para o BERT não
    /// copiá-las (homófonos: "o conserto do carro … o conserto de música"). Nas trocas do modelo de conversa fica
    /// desligado: lá a repetição é pista ("quiet quiet" → "quite quiet").
    /// </summary>
    IReadOnlyList<GrafiaProvavel> Avaliar(
        IReadOnlyList<string> palavras, int indice, IReadOnlyList<string> grafias, string idioma, bool lacunaNasRepetidas = false);
}

/// <summary>
/// BERT com cabeça de lacuna (masked LM), exportado para ONNX por <c>scripts/exportar-bert.py</c>, rodando na CPU pelo
/// ONNX Runtime. A nota de cada grafia é a pseudo-log-verossimilhança dos tokens dela: cada token é mascarado por vez,
/// com o resto da frase à vista, e somam-se os log-probabilidades do token certo. São duas notas (só o token mascarado,
/// e ele com os seguintes da mesma palavra); cada uma vira probabilidade pelo softmax entre as grafias, e vale a menor.
/// Modelos em <see cref="OpcoesTraducao.PastaModelosHomofonos"/>/&lt;idioma&gt;/model.onnx e vocab.txt.
/// </summary>
internal sealed class EscolhedorHomofonosBert(IOptions<OpcoesTraducao> opcoes) : IEscolhedorHomofonos, IDisposable
{
    /// <summary>Limite do BERT base (posições), com [CLS] e [SEP].</summary>
    private const int MaximoTokens = 512;

    private readonly OpcoesTraducao _opcoes = opcoes.Value;
    private readonly ConcurrentDictionary<string, Lazy<Modelo?>> _modelos = new(StringComparer.Ordinal);

    /// <summary>Só confere os arquivos; o modelo carrega na 1ª <see cref="Avaliar"/>.</summary>
    public bool Disponivel(string idioma) =>
        !string.IsNullOrWhiteSpace(_opcoes.PastaModelosHomofonos)
        && File.Exists(Path.Combine(Path.GetFullPath(_opcoes.PastaModelosHomofonos), Idiomas.Normalizar(idioma), "model.onnx"))
        && File.Exists(Path.Combine(Path.GetFullPath(_opcoes.PastaModelosHomofonos), Idiomas.Normalizar(idioma), "vocab.txt"));

    public IReadOnlyList<GrafiaProvavel> Avaliar(
        IReadOnlyList<string> palavras, int indice, IReadOnlyList<string> grafias, string idioma, bool lacunaNasRepetidas = false)
    {
        ArgumentNullException.ThrowIfNull(palavras);
        ArgumentNullException.ThrowIfNull(grafias);
        var modelo = ModeloDo(idioma) ?? throw new InvalidOperationException($"Sem modelo BERT para '{idioma}' em {_opcoes.PastaModelosHomofonos}.");
        var bruta = palavras[indice];
        var (inicio, fim) = Nucleo(bruta);
        var palavra = bruta[inicio..fim];
        var maiuscula = palavra.Length > 0 && char.IsUpper(palavra[0]);

        // Com lacunaNasRepetidas, a mesma palavra em outro ponto da frase vira [MASK]: senão o BERT copia a outra
        // ocorrência ("o concerto do carro … o concerto de música" deixa "conserto" com 0,29; com a lacuna, 0,66). O WordPiece separa as
        // palavras pelos espaços, então codificar uma por vez dá os mesmos tokens que a frase inteira.
        var alvo = lacunaNasRepetidas ? palavra : null;
        var idsAntes = new List<int>();
        for (var j = 0; j < indice; j++)
        {
            idsAntes.AddRange(modelo.CodificarMascarando(palavras[j], alvo));
        }

        idsAntes.AddRange(modelo.Codificar(bruta[..inicio]));
        var idsDepois = new List<int>(modelo.Codificar(bruta[fim..]));
        for (var j = indice + 1; j < palavras.Count; j++)
        {
            idsDepois.AddRange(modelo.CodificarMascarando(palavras[j], alvo));
        }

        var notas = new List<(string Grafia, double Isolada, double ComSeguintes)>();
        foreach (var g in grafias.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var grafia = maiuscula ? char.ToUpperInvariant(g[0]) + g[1..] : g;
            var idsPalavra = modelo.Codificar(grafia);
            var total = idsAntes.Count + idsPalavra.Count + idsDepois.Count + 2;
            if (idsPalavra.Count == 0 || total > MaximoTokens)
            {
                return [];
            }

            var (isolada, comSeguintes) = modelo.Nota(idsAntes, idsPalavra, idsDepois);
            notas.Add((grafia, isolada, comSeguintes));
        }

        // As duas notas erram em sentidos opostos com palavras de vários pedaços; vale a menor probabilidade, para só
        // dar certeza quando as duas concordam.
        var pIsolada = Softmax(notas.Select(n => n.Isolada).ToList());
        var pComSeguintes = Softmax(notas.Select(n => n.ComSeguintes).ToList());
        return notas.Select((n, i) => new GrafiaProvavel(n.Grafia, Math.Min(pIsolada[i], pComSeguintes[i])))
            .OrderByDescending(p => p.Probabilidade).ToList();
    }

    private static double[] Softmax(IReadOnlyList<double> notas)
    {
        var maior = notas.Max();
        var pesos = notas.Select(n => Math.Exp(n - maior)).ToArray();
        var soma = pesos.Sum();
        return [.. pesos.Select(p => p / soma)];
    }

    /// <summary>
    /// Texto antes da palavra (com a pontuação colada antes dela), o núcleo da palavra e o texto depois (com a pontuação
    /// colada depois): "rode at night." com índice 2 dá ("rode at", "night", ".").
    /// </summary>
    internal static (string Antes, string Palavra, string Depois) Partes(IReadOnlyList<string> palavras, int indice)
    {
        var bruta = palavras[indice];
        var (inicio, fim) = Nucleo(bruta);
        var antes = string.Join(' ', palavras.Take(indice).Append(bruta[..inicio])).Trim();
        var depois = (bruta[fim..] + " " + string.Join(' ', palavras.Skip(indice + 1))).Trim();
        return (antes, bruta[inicio..fim], depois);
    }

    /// <summary>Início e fim do núcleo da palavra, sem a pontuação colada antes e depois ("(night)." → 1 e 6).</summary>
    private static (int Inicio, int Fim) Nucleo(string bruta)
    {
        var inicio = 0;
        while (inicio < bruta.Length && !char.IsLetterOrDigit(bruta[inicio]))
        {
            inicio++;
        }

        var fim = bruta.Length;
        while (fim > inicio && !char.IsLetterOrDigit(bruta[fim - 1]))
        {
            fim--;
        }

        return (inicio, fim);
    }

    private Modelo? ModeloDo(string idioma)
    {
        var i = Idiomas.Normalizar(idioma);
        return _modelos.GetOrAdd(i, chave => new Lazy<Modelo?>(() => Abrir(chave))).Value;
    }

    private Modelo? Abrir(string idioma)
    {
        if (string.IsNullOrWhiteSpace(_opcoes.PastaModelosHomofonos))
        {
            return null;
        }

        var pasta = Path.Combine(Path.GetFullPath(_opcoes.PastaModelosHomofonos), idioma);
        var onnx = Path.Combine(pasta, "model.onnx");
        var vocab = Path.Combine(pasta, "vocab.txt");
        if (!File.Exists(onnx) || !File.Exists(vocab))
        {
            return null;
        }

        // Modelos "cased": sem minúsculas e sem tirar acentos, igual ao tokenizador original.
        var tokenizador = BertTokenizer.Create(vocab, new BertOptions { LowerCaseBeforeTokenization = false, RemoveNonSpacingMarks = false });
        using var sessaoOpcoes = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new Modelo(tokenizador, new InferenceSession(onnx, sessaoOpcoes));
    }

    public void Dispose()
    {
        foreach (var m in _modelos.Values.Where(l => l.IsValueCreated).Select(l => l.Value))
        {
            m?.Dispose();
        }
    }

    private sealed class Modelo(BertTokenizer tokenizador, InferenceSession sessao) : IDisposable
    {
        public IReadOnlyList<int> Codificar(string texto) =>
            texto.Length == 0 ? [] : tokenizador.EncodeToIds(texto, addSpecialTokens: false);

        /// <summary>Os tokens da palavra; se o núcleo for <paramref name="alvo"/> (sem diferenciar maiúsculas), um [MASK] no lugar dele.</summary>
        public IReadOnlyList<int> CodificarMascarando(string bruta, string? alvo)
        {
            var (inicio, fim) = Nucleo(bruta);
            if (alvo is null || fim == inicio || !string.Equals(bruta[inicio..fim], alvo, StringComparison.OrdinalIgnoreCase))
            {
                return Codificar(bruta);
            }

            return [.. Codificar(bruta[..inicio]), tokenizador.MaskingTokenId, .. Codificar(bruta[fim..])];
        }

        /// <summary>
        /// Duas notas da palavra, somando o log-probabilidade de cada token mascarado por vez (um lote por palavra):
        /// <list type="bullet">
        /// <item>Isolada: só o token mascarado. Favorece palavra rara de vários pedaços, cada um adivinhado vendo os outros
        /// ("lu" + "##v": "love" → "luv" com 0,99).</item>
        /// <item>Com os seguintes: os tokens seguintes da mesma palavra também mascarados ("PLL-word-l2r", Kauf e Ivanova,
        /// 2023). Erra para o outro lado, a favor da palavra comum ("you're" → "your", "nought" → "not").</item>
        /// </list>
        /// O último token sai igual nas duas e é calculado uma vez só.
        /// </summary>
        public (double Isolada, double ComSeguintes) Nota(IReadOnlyList<int> antes, IReadOnlyList<int> palavra, IReadOnlyList<int> depois)
        {
            var ids = new List<long> { tokenizador.ClassificationTokenId };
            ids.AddRange(antes.Select(i => (long)i));
            var inicio = ids.Count;
            ids.AddRange(palavra.Select(i => (long)i));
            ids.AddRange(depois.Select(i => (long)i));
            ids.Add(tokenizador.SeparatorTokenId);

            // Linhas 0..n-1: token k e os seguintes mascarados. Linhas n..2n-2: só o token k (k < n-1).
            int n = palavra.Count, lote = (2 * n) - 1, tamanho = ids.Count;
            var entrada = new DenseTensor<long>([lote, tamanho]);
            var mascara = new DenseTensor<long>([lote, tamanho]);
            var tipos = new DenseTensor<long>([lote, tamanho]);
            for (var linha = 0; linha < lote; linha++)
            {
                var k = linha < n ? linha : linha - n;
                var ate = linha < n ? n : k + 1;
                for (var t = 0; t < tamanho; t++)
                {
                    entrada[linha, t] = t >= inicio + k && t < inicio + ate ? tokenizador.MaskingTokenId : ids[t];
                    mascara[linha, t] = 1;
                }
            }

            using var saida = sessao.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", entrada),
                NamedOnnxValue.CreateFromTensor("attention_mask", mascara),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tipos),
            ]);
            var logits = saida.First(v => v.Name == "logits").AsTensor<float>();
            var vocabulario = logits.Dimensions[2];
            double isolada = 0, comSeguintes = 0;
            for (var linha = 0; linha < lote; linha++)
            {
                var k = linha < n ? linha : linha - n;
                var posicao = inicio + k;
                var maior = double.NegativeInfinity;
                for (var v = 0; v < vocabulario; v++)
                {
                    maior = Math.Max(maior, logits[linha, posicao, v]);
                }

                var soma = 0.0;
                for (var v = 0; v < vocabulario; v++)
                {
                    soma += Math.Exp(logits[linha, posicao, v] - maior);
                }

                var logProb = logits[linha, posicao, palavra[k]] - maior - Math.Log(soma);
                if (linha < n)
                {
                    comSeguintes += logProb;
                }

                if (linha >= n || k == n - 1)
                {
                    isolada += logProb;
                }
            }

            return (isolada, comSeguintes);
        }

        public void Dispose() => sessao.Dispose();
    }
}
