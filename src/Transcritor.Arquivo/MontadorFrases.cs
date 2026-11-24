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
/// <item>Título de capítulo ("Chapter Three", "Capítulo 2") fica junto e sozinho numa frase, mesmo quando o narrador
/// pausa entre a palavra e o número ou o Whisper não pontua o fim do título.</item>
/// <item>Pedaço que o Whisper devolve colado à palavra anterior e começando com hífen ("Super" + "-G.",
/// "fazê" + "-lo") vira uma palavra só, para a frase não sair "Super -G.".</item>
/// <item>Sentença mais longa que <see cref="DuracaoMaxima"/> é dividida na última vírgula (ou ; :), para caber na tela.</item>
/// </list>
/// Sem tempo por palavra, cai para os segmentos do motor.
/// </summary>
internal static class MontadorFrases
{
    /// <summary>Muda quando a regra de montagem muda; entra na chave do cache.</summary>
    public const int Versao = 5;

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

    /// <summary>Palavras (minúsculas) que abrem um título numerado.</summary>
    private static readonly HashSet<string> PalavrasTitulo = new(StringComparer.OrdinalIgnoreCase)
    {
        "chapter", "part", "book", "volume", "capítulo", "capitulo", "parte", "livro",
    };

    /// <summary>Números por extenso (minúsculos) aceitos depois da palavra do título.</summary>
    private static readonly HashSet<string> NumerosPorExtenso = new(StringComparer.OrdinalIgnoreCase)
    {
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty",
        "sixty", "seventy", "eighty", "ninety", "hundred",
        "um", "dois", "três", "tres", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze",
        "catorze", "quatorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove", "vinte", "trinta", "quarenta",
        "cinquenta", "sessenta", "setenta", "oitenta", "noventa", "cem",
    };

    /// <summary>
    /// Junta à anterior a palavra que começa com hífen sem espaço antes ("-G.", "-lo") seguida de letra ou número, quando
    /// a anterior termina em letra ou número. O tempo vai do começo da primeira ao fim da segunda; a confiança fica a
    /// menor das duas. Hífen solto (" - ") ou com espaço antes não junta.
    /// </summary>
    internal static List<Palavra> JuntarHifen(IReadOnlyList<Palavra> palavras)
    {
        var resultado = new List<Palavra>(palavras.Count);
        foreach (var p in palavras)
        {
            var texto = p.Texto;
            var anterior = resultado.Count > 0 ? resultado[^1].Texto.TrimEnd() : string.Empty;
            if (texto.Length > 1 && texto[0] == '-' && char.IsLetterOrDigit(texto[1])
                && anterior.Length > 0 && char.IsLetterOrDigit(anterior[^1]))
            {
                var a = resultado[^1];
                var confianca = a.Confianca is { } ca && p.Confianca is { } cp ? Math.Min(ca, cp) : a.Confianca ?? p.Confianca;
                resultado[^1] = a with { Texto = anterior + texto.TrimEnd(), Fim = Math.Max(a.Fim, p.Fim), Confianca = confianca };
                continue;
            }

            resultado.Add(p);
        }

        return resultado;
    }

    public static IReadOnlyList<Frase> Montar(TranscricaoBruta bruta)
    {
        var palavras = JuntarHifen(bruta.Palavras)
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

            // "Chapter" [pausa] "Three It was...": o número fica com o título, e o título fecha a frase.
            if (proxima is not null && atual.Count == 1 && EPalavraTitulo(palavra.Texto) && ENumero(proxima.Texto))
            {
                fecha = false;
            }
            else if (proxima is not null && atual.Count == 2 && EPalavraTitulo(atual[0].Texto) && ENumero(palavra.Texto)
                && !ComecaMinuscula(proxima.Texto))
            {
                fecha = true;
            }
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

    private static bool ComecaMinuscula(string texto)
    {
        var t = texto.TrimStart('"', '\'', '(', '“', '‘', '«');
        return t.Length > 0 && char.IsLower(t[0]);
    }

    /// <summary>"Chapter", "Capítulo"..., com maiúscula (não "part of the plan").</summary>
    private static bool EPalavraTitulo(string texto)
    {
        var t = texto.TrimEnd('.', ':', ',');
        return t.Length > 0 && char.IsUpper(t[0]) && PalavrasTitulo.Contains(t);
    }

    /// <summary>"3", "III", "Three", "Twenty-One", "Três" (com ou sem pontuação no fim).</summary>
    internal static bool ENumero(string texto)
    {
        var t = texto.TrimEnd(FechamentoIgnorado).TrimEnd('.', ',', ':', ';', '!', '?');
        if (t.Length == 0)
        {
            return false;
        }

        if (t.All(char.IsDigit) || t.All(c => "IVXLC".Contains(c, StringComparison.Ordinal)))
        {
            return true;
        }

        return t.Split('-').All(NumerosPorExtenso.Contains);
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
        palavras.Select(p => p with { Inicio = Arredondar(p.Inicio), Fim = Arredondar(p.Fim) }).ToList());

    private static List<Frase> FrasesDosSegmentos(IReadOnlyList<SegmentoBruto> segmentos) =>
        segmentos
            .OrderBy(s => s.Inicio)
            .Where(s => s.Texto.Trim().Length > 0)
            .Select(s => new Frase(Arredondar(s.Inicio), Arredondar(s.Fim), s.Texto.Trim(), null, []))
            .ToList();

    private static double Arredondar(double segundos) => Math.Round(segundos, 3, MidpointRounding.AwayFromZero);
}
