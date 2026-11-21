namespace Transcritor.Core.Modelos;

/// <summary>Contrato do JSON gerado para um arquivo.</summary>
public sealed record ResultadoTranscricao(InfoArquivo Arquivo, IReadOnlyList<Frase> Frases);

/// <param name="Hash">SHA-256 do arquivo de entrada, em hexadecimal minúsculo.</param>
/// <param name="Duracao">Duração em segundos.</param>
/// <param name="Idioma">Idioma falado, ISO 639-1 ("pt", "en").</param>
public sealed record InfoArquivo(string Hash, double Duracao, string Idioma);

/// <summary>Frase com tempos em segundos. <see cref="Traducao"/> é nula enquanto não traduzida.</summary>
public sealed record Frase(double Inicio, double Fim, string Texto, string? Traducao, IReadOnlyList<Palavra> Palavras);

/// <summary>Palavra com tempos em segundos.</summary>
public sealed record Palavra(string Texto, double Inicio, double Fim);
