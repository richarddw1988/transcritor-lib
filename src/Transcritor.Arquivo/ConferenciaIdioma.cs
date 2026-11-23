using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Confere o idioma que o Whisper detectou, entre português e inglês. Com sotaque, a detecção às vezes erra e o
/// Whisper devolve a fala já traduzida para o idioma errado ("I can find out..." vira "Eu posso descobrir...").
/// A amostra é transcrita também no outro idioma; fica o resultado cujas palavras saíram com mais confiança.
/// Medido nas gravações do usuário: a detecção acertou 15 de 20; a conferência, 20 de 20 (ver docs/modelos.md).
/// </summary>
internal static class ConferenciaIdioma
{
    /// <summary>Entra na chave do cache: mudar a regra refaz as transcrições sem idioma forçado.</summary>
    internal const int Versao = 1;

    /// <summary>O outro idioma a testar, ou nulo se o detectado não for português nem inglês.</summary>
    internal static string? Outro(string idiomaDetectado) => Idiomas.Normalizar(idiomaDetectado) switch
    {
        Idiomas.Portugues => Idiomas.Ingles,
        Idiomas.Ingles => Idiomas.Portugues,
        _ => null,
    };

    /// <summary>Média da confiança das palavras, ou nulo se o motor não informa confiança.</summary>
    internal static double? ConfiancaMedia(TranscricaoBruta transcricao)
    {
        var confiancas = transcricao.Palavras.Where(p => p.Confianca is not null).Select(p => p.Confianca!.Value).ToList();
        return confiancas.Count == 0 ? null : confiancas.Average();
    }

    /// <summary>A alternativa só ganha se as palavras dela saíram com mais confiança que as da detectada.</summary>
    internal static bool AlternativaGanha(TranscricaoBruta detectada, TranscricaoBruta alternativa) =>
        ConfiancaMedia(alternativa) is { } a && ConfiancaMedia(detectada) is { } d && a > d;
}
