using System.Buffers.Binary;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Divide o áudio longo em pedaços transcritos um por vez. No arquivo inteiro, o Whisper usa o texto anterior como
/// contexto e pode entrar em loop ("no, no, no…", a mesma frase repetida) que engole dezenas de minutos: num filme de
/// 2 h, ~35 min sumiram assim (docs/modelos.md, 2026-11-24). Cada pedaço começa do zero, então um loop não passa dele.
/// O corte cai no ponto mais silencioso perto de cada marca, para não partir palavra.
/// </summary>
internal static class PedacosAudio
{
    /// <summary>Muda quando a regra de corte muda; entra na chave do cache.</summary>
    public const int Versao = 3;

    /// <summary>
    /// Frases que o Whisper inventa em trecho sem fala (aprendidas de vídeos com créditos de legenda). Comparadas sem
    /// pontuação nem maiúsculas, com a frase inteira: "He was watching." não cai.
    /// </summary>
    private static readonly HashSet<string> FrasesInventadas = new(StringComparer.Ordinal)
    {
        "thanks for watching", "thank you for watching", "thank you so much for watching", "thanks for watching everyone",
        "please subscribe", "subtitles by the amaraorg community", "obrigado por assistir", "obrigada por assistir",
        "legendas pela comunidade amaraorg", "legendas pela comunidade do amaraorg",
    };

    /// <summary>
    /// Acima disso o texto do pedaço é repetitivo demais (razão de compressão do zlib, o mesmo limite do Whisper):
    /// loop, como "peraí, peraí, …" por 10 s quando a pessoa disse "peraí" três vezes.
    /// </summary>
    public const double RazaoCompressaoMaxima = 2.4;

    /// <summary>Temperaturas das novas tentativas de um pedaço em loop, em ordem (as do Whisper original).</summary>
    public static readonly IReadOnlyList<double> TemperaturasReserva = [0.2, 0.4, 0.6, 0.8, 1.0];

    /// <summary>Bytes do texto (UTF-8) divididos pelos bytes comprimidos com zlib; 0 sem texto.</summary>
    public static double RazaoCompressao(TranscricaoBruta bruta)
    {
        ArgumentNullException.ThrowIfNull(bruta);
        var texto = string.Concat(bruta.Segmentos.Select(s => s.Texto)).Trim();
        if (texto.Length == 0)
        {
            return 0;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(texto);
        using var saida = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(saida, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return bytes.Length / (double)saida.Length;
    }

    /// <summary>Resolução da busca pelo silêncio.</summary>
    public const double PassoEnergia = 0.1;

    /// <summary>Janela de silêncio procurada (5 passos = 0,5 s); o corte fica no meio dela.</summary>
    private const int PassosJanela = 5;

    /// <summary>Energia (média dos quadrados) de cada 0,1 s de um WAV PCM 16 bits mono.</summary>
    /// <exception cref="InvalidDataException">O arquivo não é um WAV PCM 16 bits mono.</exception>
    public static async Task<double[]> EnergiaAsync(string caminhoWav, CancellationToken cancellationToken) =>
        Energia(await File.ReadAllBytesAsync(caminhoWav, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc cref="EnergiaAsync"/>
    public static double[] Energia(byte[] bytes)
    {
        var (formato, inicio, tamanho) = Midia.RecorteWav.LerChunks(bytes);
        var canais = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(2));
        var taxa = BinaryPrimitives.ReadInt32LittleEndian(formato.AsSpan(4));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(formato.AsSpan(14));
        if (canais != 1 || bits != 16 || taxa <= 0)
        {
            throw new InvalidDataException("Esperado WAV PCM 16 bits mono.");
        }

        var amostrasPorPasso = (int)(taxa * PassoEnergia);
        var amostras = tamanho / 2;
        var energia = new double[(int)Math.Ceiling(amostras / (double)amostrasPorPasso)];
        for (var p = 0; p < energia.Length; p++)
        {
            var de = p * (long)amostrasPorPasso;
            var ate = Math.Min(de + amostrasPorPasso, amostras);
            double soma = 0;
            for (var a = de; a < ate; a++)
            {
                double s = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(inicio + (int)(a * 2)));
                soma += s * s;
            }

            energia[p] = ate > de ? soma / (ate - de) : 0;
        }

        return energia;
    }

    /// <summary>
    /// Limites [início, fim] dos pedaços: um corte perto de cada múltiplo de <paramref name="tamanho"/>, no ponto mais
    /// silencioso a até <paramref name="busca"/> segundos. Sem divisão (um pedaço só) se a duração couber em 1,5 pedaço.
    /// </summary>
    public static IReadOnlyList<(double Inicio, double Fim)> Limites(double[] energia, double duracao, double tamanho, double busca)
    {
        if (tamanho <= 0 || duracao <= tamanho * 1.5)
        {
            return [(0, duracao)];
        }

        var cortes = new List<double> { 0 };
        for (var alvo = tamanho; alvo < duracao - (tamanho / 2); alvo += tamanho)
        {
            var de = Math.Max(cortes[^1] + busca, alvo - busca);
            var corte = MaisSilencioso(energia, de, alvo + busca) ?? alvo;
            cortes.Add(corte);
        }

        cortes.Add(duracao);
        return cortes.Zip(cortes.Skip(1)).Select(c => (c.First, c.Second)).ToList();
    }

    /// <summary>Meio da janela de 0,5 s com menor energia entre <paramref name="de"/> e <paramref name="ate"/>.</summary>
    private static double? MaisSilencioso(double[] energia, double de, double ate)
    {
        var primeiro = Math.Max(0, (int)(de / PassoEnergia));
        var ultimo = Math.Min(energia.Length - PassosJanela, (int)(ate / PassoEnergia));
        if (ultimo < primeiro)
        {
            return null;
        }

        var melhor = primeiro;
        var menor = double.MaxValue;
        for (var p = primeiro; p <= ultimo; p++)
        {
            double soma = 0;
            for (var k = 0; k < PassosJanela; k++)
            {
                soma += energia[p + k];
            }

            if (soma < menor)
            {
                menor = soma;
                melhor = p;
            }
        }

        return Math.Round((melhor + (PassosJanela / 2.0)) * PassoEnergia, 3);
    }

    /// <summary>Tira as frases inventadas inteiras (<see cref="FrasesInventadas"/>): os segmentos e as palavras deles.</summary>
    public static TranscricaoBruta SemFrasesInventadas(TranscricaoBruta bruta)
    {
        var inventados = bruta.Segmentos.Where(s => FrasesInventadas.Contains(Normalizar(s.Texto))).ToList();
        var palavras = bruta.Palavras.ToList();

        // Também por palavras, porque o segmento pode trazer mais de uma frase ("Yeah! Thanks for watching!").
        var remover = new bool[palavras.Count];
        for (var i = 0; i < palavras.Count; i++)
        {
            if (i > 0 && !TerminaFrase(palavras[i - 1].Texto))
            {
                continue;
            }

            var texto = string.Empty;
            for (var j = i; j < palavras.Count && j < i + 8; j++)
            {
                texto += palavras[j].Texto + " ";
                if (TerminaFrase(palavras[j].Texto) || j == palavras.Count - 1)
                {
                    if (FrasesInventadas.Contains(Normalizar(texto)))
                    {
                        Array.Fill(remover, true, i, j - i + 1);
                    }

                    break;
                }
            }
        }

        if (inventados.Count == 0 && !remover.Contains(true))
        {
            return bruta;
        }

        return bruta with
        {
            Segmentos = bruta.Segmentos.Except(inventados).ToList(),
            Palavras = palavras.Where((p, i) => !remover[i]).ToList(),
        };
    }

    /// <summary>Últimas palavras do pedaço (até ~40, terminando em frase), para dar o estilo ao seguinte.</summary>
    public static string? ContextoDoFim(TranscricaoBruta bruta)
    {
        var texto = string.Concat(bruta.Palavras.TakeLast(40).Select(p => p.Texto.StartsWith(' ') ? p.Texto : " " + p.Texto)).Trim();
        return texto.Length == 0 ? null : texto;
    }

    private static bool TerminaFrase(string palavra)
    {
        var t = palavra.TrimEnd().TrimEnd('"', '\'', '”', '’', ')');
        return t.Length > 0 && ".!?…".Contains(t[^1], StringComparison.Ordinal);
    }

    private static string Normalizar(string texto) =>
        string.Join(' ', new string(texto.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Junta as transcrições dos pedaços, somando o início de cada pedaço aos tempos.</summary>
    public static TranscricaoBruta Juntar(IReadOnlyList<(double Inicio, double Fim, TranscricaoBruta Bruta)> pedacos, double duracao)
    {
        var segmentos = new List<SegmentoBruto>();
        var palavras = new List<Palavra>();
        foreach (var (inicio, fim, bruta) in pedacos)
        {
            segmentos.AddRange(bruta.Segmentos.Select(s => s with
            {
                Inicio = Math.Round(inicio + s.Inicio, 3),
                Fim = Math.Round(Math.Min(inicio + s.Fim, fim), 3),
            }));
            palavras.AddRange(bruta.Palavras.Select(p => p with
            {
                Inicio = Math.Round(inicio + p.Inicio, 3),
                Fim = Math.Round(Math.Min(inicio + p.Fim, fim), 3),
            }));
        }

        return new TranscricaoBruta(pedacos[0].Bruta.Idioma, duracao, segmentos, palavras);
    }
}
