namespace Transcritor.Core;

/// <summary>Códigos ISO 639-1 usados pela lib.</summary>
public static class Idiomas
{
    public const string Portugues = "pt";
    public const string Ingles = "en";

    /// <summary>
    /// Normaliza um código ("PT", "pt-BR", "english", "por", "eng") para "pt" ou "en"; outros idiomas voltam em minúsculas.
    /// </summary>
    public static string Normalizar(string codigo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codigo);
        var c = codigo.Trim().ToLowerInvariant();
        return c switch
        {
            "pt" or "por" or "portuguese" or "portugues" or "português" => Portugues,
            "en" or "eng" or "english" or "ingles" or "inglês" => Ingles,
            _ when c.StartsWith("pt-", StringComparison.Ordinal) => Portugues,
            _ when c.StartsWith("en-", StringComparison.Ordinal) => Ingles,
            _ => c,
        };
    }
}
