using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Confere no áudio uma troca proposta pelo corretor de texto. A frase é transcrita de novo com a versão trocada como
/// "prompt" do Whisper: o Whisper tende a copiar o prompt, mas quando o som não bate a palavra copiada sai com
/// confiança perto de zero ("gizzard" → "lizard": 0,00003), e quando bate sai com confiança normal ("invaded" →
/// "invited": 0,27). A troca só vale se a candidata aparecer na mesma posição com confiança acima do limite e o resto da frase
/// reouvida bater (senão o Whisper inventou outra frase).
/// </summary>
internal static class ConferenciaCorrecao
{
    /// <summary>Muda quando a regra muda; entra na chave do cache da correção.</summary>
    public const int Versao = 2; // 2: a frase reouvida tem de bater com a maioria das outras palavras (2026-11-23)

    /// <summary>Folga, em segundos, antes e depois da frase no recorte reouvido.</summary>
    public const double Margem = 0.3;

    /// <summary>
    /// Parte mínima das outras palavras da frase trocada que a transcrição nova precisa repetir. Abaixo disso o Whisper
    /// inventou outra frase (no FLEURS, "can be a good time to..." reouvida como "I'll see you next time. Bye!" aprovou
    /// "theme" → "time" só pelo "time" solto).
    /// </summary>
    public const double MinimoOutrasIguais = 0.6;

    /// <summary>
    /// A palavra que a transcrição nova pôs na posição <paramref name="indice"/> da frase trocada é a candidata, com
    /// confiança de pelo menos <paramref name="limite"/>, e o resto da frase saiu quase igual?
    /// </summary>
    public static bool Confirma(IReadOnlyList<string> fraseTrocada, int indice, TranscricaoBruta reouvida, double limite)
    {
        var esperadas = fraseTrocada.Select(Nucleo).ToList();
        var ouvidas = reouvida.Palavras.Select(p => (Texto: Nucleo(p.Texto), p.Confianca)).ToList();
        var candidata = esperadas[indice];
        if (candidata.Length == 0)
        {
            return false;
        }

        // Distância de edição por palavra; depois volta pelo caminho inteiro, achando o par da posição pedida e
        // contando as outras palavras que saíram iguais.
        var custo = new int[esperadas.Count + 1, ouvidas.Count + 1];
        for (var i = 0; i <= esperadas.Count; i++)
        {
            custo[i, 0] = i;
        }

        for (var j = 0; j <= ouvidas.Count; j++)
        {
            custo[0, j] = j;
        }

        for (var i = 1; i <= esperadas.Count; i++)
        {
            for (var j = 1; j <= ouvidas.Count; j++)
            {
                var troca = custo[i - 1, j - 1] + (esperadas[i - 1] == ouvidas[j - 1].Texto ? 0 : 1);
                custo[i, j] = Math.Min(troca, Math.Min(custo[i - 1, j] + 1, custo[i, j - 1] + 1));
            }
        }

        var confirmou = false;
        var outrasIguais = 0;
        int a = esperadas.Count, b = ouvidas.Count;
        while (a > 0 && b > 0)
        {
            var igual = esperadas[a - 1] == ouvidas[b - 1].Texto;
            if (custo[a, b] == custo[a - 1, b - 1] + (igual ? 0 : 1))
            {
                if (a - 1 == indice)
                {
                    confirmou = igual && (ouvidas[b - 1].Confianca ?? 0) >= limite;
                }
                else if (igual)
                {
                    outrasIguais++;
                }

                a--;
                b--;
            }
            else if (custo[a, b] == custo[a - 1, b] + 1)
            {
                a--;
            }
            else
            {
                b--;
            }
        }

        var outras = esperadas.Count(e => e.Length > 0) - 1;
        return confirmou && (outras == 0 || outrasIguais >= MinimoOutrasIguais * outras);
    }

    private static string Nucleo(string palavra) =>
        new string(palavra.Replace('’', '\'').Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray())
            .Trim('\'')
            .ToLowerInvariant();
}
