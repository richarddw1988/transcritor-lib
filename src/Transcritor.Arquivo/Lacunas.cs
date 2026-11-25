using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Segunda passada nas lacunas, só com a voz separada (<see cref="OpcoesArquivo.PreencherLacunas"/>): trechos em que a
/// voz tem energia mas o Whisper não pôs nenhuma palavra (gritos, falas curtas: "No!", "You yield.") são transcritos de
/// novo, sozinhos. Com a música e os efeitos tirados, energia na faixa da voz é quase sempre fala; mesmo assim o
/// Whisper inventa frase em parte desses trechos ("Thank you for watching"), por isso só entra o resultado com pouca
/// chance de ser silêncio (<see cref="SemFalaMaxima"/>) e que não seja pedaço de frase inventada. No filme de 2 h:
/// erro 10,7% → 9,5%, nomes 88% → 91% (docs/modelos.md).
/// </summary>
internal static class Lacunas
{
    /// <summary>Muda o resultado; entra na chave do cache.</summary>
    public const int Versao = 1;

    /// <summary>Energia mínima da voz separada, em dB abaixo do máximo (−40 dB: 75% dos décimos de segundo do filme).</summary>
    public const double LimiarDb = -40;

    /// <summary>Folga em volta de cada palavra transcrita que já conta como coberta.</summary>
    private const double FolgaPalavra = 0.3;

    /// <summary>Silêncio tolerado dentro de uma lacuna (em passos de 0,1 s).</summary>
    private const int BuracoMaximo = 3;

    /// <summary>Duração mínima de uma lacuna.</summary>
    public const double DuracaoMinima = 0.5;

    /// <summary>Contexto de áudio em volta da lacuna mandado ao Whisper.</summary>
    public const double Contexto = 0.5;

    /// <summary>
    /// Maior chance de silêncio (no_speech_prob do Whisper) aceita. Medido no filme: 0,1 → 9,2%; 0,15 → 8,9%;
    /// 0,2 → 9,0%; 0,3 → 9,4%; sem corte → 10,5% (as frases inventadas entram).
    /// </summary>
    public const double SemFalaMaxima = 0.2;

    /// <summary>Lacunas [início, fim]: voz acima do limiar sem palavra transcrita por perto.</summary>
    /// <param name="energia">Energia (média dos quadrados) de cada 0,1 s, de <see cref="PedacosAudio.Energia"/>.</param>
    /// <param name="palavras">Palavras já transcritas.</param>
    public static IReadOnlyList<(double Inicio, double Fim)> Detectar(double[] energia, IReadOnlyList<Palavra> palavras)
    {
        ArgumentNullException.ThrowIfNull(energia);
        ArgumentNullException.ThrowIfNull(palavras);
        var coberto = new bool[energia.Length];
        foreach (var p in palavras)
        {
            var de = Math.Max(0, (int)((p.Inicio - FolgaPalavra) / PedacosAudio.PassoEnergia));
            var ate = Math.Min(energia.Length - 1, (int)((p.Fim + FolgaPalavra) / PedacosAudio.PassoEnergia));
            for (var k = de; k <= ate; k++)
            {
                coberto[k] = true;
            }
        }

        // Média dos quadrados de amostras de 16 bits: 0 dB = escala cheia.
        var limiar = Math.Pow(10, LimiarDb / 10) * 32768d * 32768d;
        var lacunas = new List<(double, double)>();
        int? inicio = null;
        var ultimo = 0;
        for (var k = 0; k < energia.Length; k++)
        {
            if (energia[k] < limiar || coberto[k])
            {
                continue;
            }

            if (inicio is null || k - ultimo > BuracoMaximo)
            {
                Fechar();
                inicio = k;
            }

            ultimo = k;
        }

        Fechar();
        return lacunas;

        void Fechar()
        {
            if (inicio is { } i && (ultimo + 1 - i) * PedacosAudio.PassoEnergia >= DuracaoMinima - 1e-9)
            {
                lacunas.Add((Math.Round(i * PedacosAudio.PassoEnergia, 3), Math.Round((ultimo + 1) * PedacosAudio.PassoEnergia, 3)));
            }
        }
    }

    /// <summary>
    /// Palavras e segmentos da transcrição do recorte [<paramref name="inicioRecorte"/>, …] que caem na lacuna, já no
    /// tempo do arquivo; vazio se o resultado parece silêncio ou frase inventada.
    /// </summary>
    public static TranscricaoBruta? Aceitar(TranscricaoBruta nova, double inicioRecorte, double inicio, double fim)
    {
        ArgumentNullException.ThrowIfNull(nova);
        if (nova.Segmentos.Count == 0 || nova.Segmentos.Any(s => s.SemFala is null or >= SemFalaMaxima))
        {
            return null;
        }

        var palavras = nova.Palavras
            .Select(p => p with { Inicio = Math.Round(inicioRecorte + p.Inicio, 3), Fim = Math.Round(inicioRecorte + p.Fim, 3) })
            .Where(p => (p.Inicio + p.Fim) / 2 >= inicio - 0.2 && (p.Inicio + p.Fim) / 2 <= fim + 0.2)
            .ToList();
        if (palavras.Count == 0)
        {
            return null;
        }

        var texto = string.Concat(palavras.Select(p => p.Texto.StartsWith(' ') ? p.Texto : " " + p.Texto)).Trim();
        if (PedacosAudio.PedacoDeFraseInventada(texto))
        {
            return null;
        }

        return nova with
        {
            Segmentos = [new SegmentoBruto(palavras[0].Inicio, palavras[^1].Fim, " " + texto)],
            Palavras = palavras,
        };
    }

    /// <summary>Acrescenta as palavras e segmentos aceitos, mantendo a ordem pelo tempo.</summary>
    public static TranscricaoBruta Juntar(TranscricaoBruta bruta, IReadOnlyList<TranscricaoBruta> novas)
    {
        ArgumentNullException.ThrowIfNull(bruta);
        ArgumentNullException.ThrowIfNull(novas);
        if (novas.Count == 0)
        {
            return bruta;
        }

        return bruta with
        {
            Segmentos = bruta.Segmentos.Concat(novas.SelectMany(n => n.Segmentos)).OrderBy(s => s.Inicio).ToList(),
            Palavras = bruta.Palavras.Concat(novas.SelectMany(n => n.Palavras)).OrderBy(p => p.Inicio).ToList(),
        };
    }
}
