using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Acha e conserta trechos em que o Whisper se perdeu na transcrição longa (com o texto anterior como contexto):
/// <list type="bullet">
/// <item>repetição ou palavras inventadas ("I'm going to chop faster and faster." 4 vezes), que voltam com duração
/// zero porque o alinhamento não acha som para elas;</item>
/// <item>trecho longo sem nenhuma pontuação (o modelo "desliga" a pontuação e às vezes repete palavras).</item>
/// </list>
/// O trecho em volta é transcrito de novo, isolado, e substitui o original se ficar melhor.
/// </summary>
internal static class ReparadorTrechos
{
    /// <summary>Muda o resultado; entra na chave do cache quando o reparo está ligado.</summary>
    public const int Versao = 3;

    /// <summary>Palavra com duração menor que isso não tem som alinhado.</summary>
    public const double DuracaoSuspeita = 0.02;

    /// <summary>Quantas palavras suspeitas, perto umas das outras, formam um trecho a refazer.</summary>
    public const int MinimoSuspeitas = 3;

    /// <summary>Segundos seguidos sem nenhuma pontuação que tornam o trecho suspeito.</summary>
    public const double SemPontuacaoMaxima = 15;

    /// <summary>Palavras normais toleradas entre duas suspeitas do mesmo trecho.</summary>
    private const int FolgaEntreSuspeitas = 2;

    /// <summary>Contexto procurado antes e depois do trecho para cortar o áudio numa pausa.</summary>
    private const double ContextoMaximo = 8;

    /// <summary>Contexto mínimo preferido (o Whisper erra menos com algumas palavras em volta).</summary>
    private const double ContextoMinimo = 1.5;

    /// <summary>Recorte do áudio, em segundos, a transcrever de novo.</summary>
    public sealed record Janela(double Inicio, double Fim);

    public static bool Suspeita(Palavra p) => p.Fim - p.Inicio < DuracaoSuspeita;

    private static bool TemPontuacao(Palavra p)
    {
        var t = p.Texto.TrimEnd().TrimEnd('"', '\'', '”', '’', ')');
        return t.Length > 0 && ".,!?;:…".Contains(t[^1], StringComparison.Ordinal);
    }

    /// <summary>Janelas (já unidas quando se sobrepõem) em volta de cada trecho suspeito, cortadas em pausas.</summary>
    public static IReadOnlyList<Janela> Detectar(IReadOnlyList<Palavra> palavras, double duracao)
    {
        var janelas = new List<Janela>();
        var i = 0;
        while (i < palavras.Count)
        {
            if (!Suspeita(palavras[i]))
            {
                i++;
                continue;
            }

            // Agrupa as suspeitas próximas: [primeira, ultima].
            var primeira = i;
            var ultima = i;
            var quantas = 1;
            var j = i + 1;
            while (j < palavras.Count && j - ultima <= FolgaEntreSuspeitas + 1)
            {
                if (Suspeita(palavras[j]))
                {
                    ultima = j;
                    quantas++;
                }

                j++;
            }

            if (quantas >= MinimoSuspeitas)
            {
                janelas.Add(new Janela(CorteAntes(palavras, primeira), CorteDepois(palavras, ultima, duracao)));
            }

            i = ultima + 1;
        }

        foreach (var (primeira, ultima) in TrechosSemPontuacao(palavras))
        {
            janelas.Add(new Janela(CorteAntes(palavras, primeira), CorteDepois(palavras, ultima, duracao)));
        }

        return Unir(janelas);
    }

    /// <summary>
    /// Troca as palavras e segmentos da janela pelos da transcrição nova (com tempos relativos ao recorte).
    /// </summary>
    public static TranscricaoBruta Substituir(TranscricaoBruta original, Janela janela, TranscricaoBruta nova)
    {
        var palavras = original.Palavras.Where(p => !Dentro(p.Inicio, p.Fim, janela))
            .Concat(nova.Palavras.Select(p => p with
            {
                Inicio = Math.Round(janela.Inicio + p.Inicio, 3),
                Fim = Math.Round(Math.Min(janela.Inicio + p.Fim, janela.Fim), 3),
            }))
            .OrderBy(p => p.Inicio)
            .ToList();
        var segmentos = original.Segmentos.Where(s => !Dentro(s.Inicio, s.Fim, janela))
            .Concat(nova.Segmentos.Select(s => s with
            {
                Inicio = Math.Round(janela.Inicio + s.Inicio, 3),
                Fim = Math.Round(Math.Min(janela.Inicio + s.Fim, janela.Fim), 3),
            }))
            .OrderBy(s => s.Inicio)
            .ToList();
        return original with { Palavras = palavras, Segmentos = segmentos };
    }

    /// <summary>A transcrição nova só entra se tiver palavras e ficar melhor que a original na mesma janela.</summary>
    public static bool Melhorou(TranscricaoBruta original, Janela janela, TranscricaoBruta nova)
    {
        if (nova.Palavras.Count == 0)
        {
            return false;
        }

        var trecho = original.Palavras.Where(p => Dentro(p.Inicio, p.Fim, janela)).ToList();
        var antes = trecho.Count(Suspeita);
        var depois = nova.Palavras.Count(Suspeita);
        // Não aceita troca que perde a pontuação (recorte sem contexto às vezes sai todo em minúsculas e sem vírgulas).
        var semPontuacaoAntes = MaiorTrechoSemPontuacao(trecho);
        var semPontuacaoDepois = MaiorTrechoSemPontuacao(nova.Palavras);
        if (semPontuacaoDepois >= SemPontuacaoMaxima && semPontuacaoDepois > semPontuacaoAntes)
        {
            return false;
        }

        // Janela com palavras sem som: precisa de menos delas. Janela só sem pontuação: precisa de trecho sem pontuação menor.
        return depois < antes || (antes == 0 && depois == 0 && semPontuacaoDepois < semPontuacaoAntes);
    }

    /// <summary>Texto das últimas palavras boas antes da janela (até ~40), para o motor manter o estilo e a pontuação.</summary>
    public static string ContextoAntes(IReadOnlyList<Palavra> palavras, Janela janela)
    {
        var antes = palavras.Where(p => p.Fim <= janela.Inicio && !Suspeita(p)).TakeLast(40);
        return string.Concat(antes.Select(p => p.Texto)).Trim();
    }

    /// <summary>Índices [primeira, ultima] de cada sequência de palavras sem pontuação que dura 15 s ou mais.</summary>
    private static IEnumerable<(int Primeira, int Ultima)> TrechosSemPontuacao(IReadOnlyList<Palavra> palavras)
    {
        var inicio = 0;
        for (var i = 0; i < palavras.Count; i++)
        {
            var fecha = TemPontuacao(palavras[i]) || i == palavras.Count - 1;
            if (!fecha)
            {
                continue;
            }

            // A palavra com pontuação faz parte do trecho (é onde ele termina).
            if (SemPontuacao(palavras, inicio, i) >= SemPontuacaoMaxima)
            {
                yield return (inicio, i);
            }

            inicio = i + 1;
        }
    }

    /// <summary>Duração entre o início da primeira e o início da última palavra (o tempo falado sem pontuar).</summary>
    private static double SemPontuacao(IReadOnlyList<Palavra> palavras, int primeira, int ultima) =>
        palavras[ultima].Inicio - palavras[primeira].Inicio;

    private static double MaiorTrechoSemPontuacao(IReadOnlyList<Palavra> palavras)
    {
        double maior = 0;
        var inicio = 0;
        for (var i = 0; i < palavras.Count; i++)
        {
            if (TemPontuacao(palavras[i]) || i == palavras.Count - 1)
            {
                maior = Math.Max(maior, SemPontuacao(palavras, inicio, i));
                inicio = i + 1;
            }
        }

        return maior;
    }

    private static bool Dentro(double inicio, double fim, Janela janela)
    {
        var meio = (inicio + fim) / 2;
        return meio >= janela.Inicio && meio < janela.Fim;
    }

    /// <summary>Corta no meio da maior pausa entre 1,5 e 8 s antes do trecho (ou a maior até 8 s, ou o começo).</summary>
    private static double CorteAntes(IReadOnlyList<Palavra> palavras, int primeira)
    {
        if (primeira == 0)
        {
            return 0;
        }

        // Âncora: fim da última palavra boa antes do trecho (os tempos das suspeitas não valem).
        var ancora = palavras[primeira - 1].Fim;
        double? melhor = null;
        double melhorPausa = -1;
        var melhorPreferido = false;
        for (var k = primeira - 1; k >= 1 && ancora - palavras[k].Inicio <= ContextoMaximo; k--)
        {
            var pausa = palavras[k].Inicio - palavras[k - 1].Fim;
            var preferido = ancora - palavras[k].Inicio >= ContextoMinimo;
            if ((preferido && !melhorPreferido) || (preferido == melhorPreferido && pausa > melhorPausa))
            {
                melhor = (palavras[k - 1].Fim + palavras[k].Inicio) / 2;
                melhorPausa = pausa;
                melhorPreferido = preferido;
            }
        }

        return Math.Round(Math.Max(0, melhor ?? palavras[0].Inicio - 0.2), 3);
    }

    /// <summary>Corta no meio da maior pausa entre 1,5 e 8 s depois do trecho (ou a maior até 8 s, ou o fim).</summary>
    private static double CorteDepois(IReadOnlyList<Palavra> palavras, int ultima, double duracao)
    {
        var fimDoAudio = Math.Max(duracao, palavras[^1].Fim);
        if (ultima >= palavras.Count - 1)
        {
            return Math.Round(fimDoAudio, 3);
        }

        var ancora = palavras[ultima + 1].Inicio;
        double? melhor = null;
        double melhorPausa = -1;
        var melhorPreferido = false;
        for (var k = ultima + 1; k < palavras.Count - 1 && palavras[k].Fim - ancora <= ContextoMaximo; k++)
        {
            var pausa = palavras[k + 1].Inicio - palavras[k].Fim;
            var preferido = palavras[k].Fim - ancora >= ContextoMinimo;
            if ((preferido && !melhorPreferido) || (preferido == melhorPreferido && pausa > melhorPausa))
            {
                melhor = (palavras[k].Fim + palavras[k + 1].Inicio) / 2;
                melhorPausa = pausa;
                melhorPreferido = preferido;
            }
        }

        return Math.Round(Math.Min(fimDoAudio, melhor ?? palavras[^1].Fim + 0.2), 3);
    }

    private static List<Janela> Unir(List<Janela> janelas)
    {
        var unidas = new List<Janela>();
        foreach (var j in janelas.OrderBy(j => j.Inicio))
        {
            if (unidas.Count > 0 && j.Inicio <= unidas[^1].Fim)
            {
                unidas[^1] = unidas[^1] with { Fim = Math.Max(unidas[^1].Fim, j.Fim) };
            }
            else
            {
                unidas.Add(j);
            }
        }

        return unidas;
    }
}
