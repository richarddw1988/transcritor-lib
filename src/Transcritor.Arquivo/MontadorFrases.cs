using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Monta as frases do JSON a partir das palavras do motor. Uma frase é uma sentença completa:
/// os segmentos do Whisper cortam sentenças longas no meio, e traduzir pedaços faz o modelo
/// misturar conteúdo da continuação (visto em teste com um audiolivro).
/// <list type="bullet">
/// <item>Termina em pontuação final (. ? ! …), exceto depois de abreviações (Mr., Mrs., St., Sr., Dra. ...),
/// iniciais (J.) e quando a próxima palavra começa com minúscula.</item>
/// <item>Termina também numa pausa longa (<see cref="PausaQueFechaFrase"/>), para fala sem pontuação.</item>
/// <item>Sentença mais longa que <see cref="DuracaoMaxima"/> é dividida na última vírgula (ou ; :), para caber na tela.</item>
/// </list>
/// Sem tempo por palavra, cai para os segmentos do motor.
/// </summary>
internal static class MontadorFrases
{
    /// <summary>Muda quando a regra de montagem muda; entra na chave do cache.</summary>
    public const int Versao = 2;

    public static readonly TimeSpan PausaQueFechaFrase = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan DuracaoMaxima = TimeSpan.FromSeconds(30);

    private static readonly char[] PontuacaoFinal = ['.', '?', '!', '…'];
    private static readonly char[] PontuacaoPausa = [',', ';', ':'];
    private static readonly char[] FechamentoIgnorado = ['"', '\'', ')', ']', '»', '”', '’'];

    /// <summary>Abreviações (sem o ponto, minúsculas) que não terminam frase.</summary>
    private static readonly HashSet<string> Abreviacoes = new(StringComparer.OrdinalIgnoreCase)
    {
        // inglês
        // Só títulos e abreviações que quase nunca fecham sentença; palavras comuns ("no", "mar") e
        // abreviações que costumam terminar frase ("p.m.", "U.S.", "etc.") ficam de fora.
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "vs", "vol", "fig", "approx", "dept", "gov", "lt", "capt", "rev", "hon",
        "e.g", "i.e",
        // português
        "sra", "srta", "dra", "profa", "av", "sto", "sta", "nº", "pág", "exmo", "exma", "ilmo", "ilma", "v.exa",
    };

    public static IReadOnlyList<Frase> Montar(TranscricaoBruta bruta)
    {
        var palavras = bruta.Palavras
            .Select(p => p with { Texto = p.Texto.Trim() })
            .Where(p => p.Texto.Length > 0)
            .OrderBy(p => p.Inicio)
            .ToList();

        if (palavras.Count == 0)
        {
            return FrasesDosSegmentos(bruta.Segmentos);
        }

        var frases = new List<Frase>();
        var atual = new List<Palavra>();
        for (var i = 0; i < palavras.Count; i++)
        {
            var palavra = palavras[i];
            var proxima = i + 1 < palavras.Count ? palavras[i + 1] : null;
            atual.Add(palavra);

            var fecha = proxima is null
                || TerminaSentenca(palavra.Texto, proxima.Texto)
                || proxima.Inicio - palavra.Fim >= PausaQueFechaFrase.TotalSeconds;
            if (fecha)
            {
                frases.AddRange(DividirSeLonga(atual));
                atual = [];
            }
        }

        return frases;
    }

    /// <summary>Pontuação final que não é abreviação nem inicial, seguida de palavra que não começa com minúscula.</summary>
    internal static bool TerminaSentenca(string texto, string? proximo)
    {
        var t = texto.TrimEnd(FechamentoIgnorado);
        if (t.Length == 0 || Array.IndexOf(PontuacaoFinal, t[^1]) < 0)
        {
            return false;
        }

        if (t[^1] == '.')
        {
            var semPonto = t.TrimEnd('.').TrimStart('(', '"', '\'', '“', '‘', '«');
            if (Abreviacoes.Contains(semPonto))
            {
                return false;
            }

            // Inicial ("J." em "J. R. R. Tolkien").
            if (semPonto.Length == 1 && char.IsUpper(semPonto[0]))
            {
                return false;
            }
        }

        // "e.g. something", "3 p.m. today": a sentença continua.
        var inicioProximo = proximo?.TrimStart('"', '\'', '(', '“', '‘', '«', '¿', '¡');
        return string.IsNullOrEmpty(inicioProximo) || !char.IsLower(inicioProximo[0]);
    }

    private static IEnumerable<Frase> DividirSeLonga(List<Palavra> palavras)
    {
        var restantes = palavras;
        while (restantes.Count > 1 && restantes[^1].Fim - restantes[0].Inicio > DuracaoMaxima.TotalSeconds)
        {
            // Último ponto de corte (vírgula, ponto e vírgula, dois-pontos) que ainda cabe na duração máxima.
            var limite = restantes[0].Inicio + DuracaoMaxima.TotalSeconds;
            var corte = -1;
            for (var i = 0; i < restantes.Count - 1 && restantes[i].Fim <= limite; i++)
            {
                var t = restantes[i].Texto.TrimEnd(FechamentoIgnorado);
                if (t.Length > 0 && Array.IndexOf(PontuacaoPausa, t[^1]) >= 0)
                {
                    corte = i;
                }
            }

            if (corte < 0)
            {
                // Sem vírgula: corta na maior pausa entre palavras dentro do limite.
                var maiorPausa = -1d;
                for (var i = 0; i < restantes.Count - 1 && restantes[i].Fim <= limite; i++)
                {
                    var pausa = restantes[i + 1].Inicio - restantes[i].Fim;
                    if (pausa > maiorPausa)
                    {
                        maiorPausa = pausa;
                        corte = i;
                    }
                }
            }

            if (corte < 0)
            {
                corte = 0;
            }

            yield return CriarFrase(restantes.Take(corte + 1).ToList());
            restantes = restantes.Skip(corte + 1).ToList();
        }

        if (restantes.Count > 0)
        {
            yield return CriarFrase(restantes);
        }
    }

    private static Frase CriarFrase(List<Palavra> palavras) => new(
        Arredondar(palavras[0].Inicio),
        Arredondar(palavras[^1].Fim),
        string.Join(' ', palavras.Select(p => p.Texto)),
        null,
        palavras.Select(p => new Palavra(p.Texto, Arredondar(p.Inicio), Arredondar(p.Fim))).ToList());

    private static List<Frase> FrasesDosSegmentos(IReadOnlyList<SegmentoBruto> segmentos) =>
        segmentos
            .OrderBy(s => s.Inicio)
            .Where(s => s.Texto.Trim().Length > 0)
            .Select(s => new Frase(Arredondar(s.Inicio), Arredondar(s.Fim), s.Texto.Trim(), null, []))
            .ToList();

    private static double Arredondar(double segundos) => Math.Round(segundos, 3, MidpointRounding.AwayFromZero);
}
