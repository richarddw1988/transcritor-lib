namespace Transcritor.Core.Modelos;

/// <summary>Contrato do JSON gerado para um arquivo.</summary>
public sealed record ResultadoTranscricao(InfoArquivo Arquivo, IReadOnlyList<Frase> Frases);

/// <param name="Hash">SHA-256 do arquivo de entrada, em hexadecimal minúsculo.</param>
/// <param name="Duracao">Duração em segundos.</param>
/// <param name="Idioma">Idioma falado, ISO 639-1 ("pt", "en").</param>
public sealed record InfoArquivo(string Hash, double Duracao, string Idioma);

/// <summary>Frase (sentença completa) com tempos em segundos.</summary>
/// <param name="Inicio">Início em segundos.</param>
/// <param name="Fim">Fim em segundos.</param>
/// <param name="Texto">Texto no idioma falado.</param>
/// <param name="Traducao">Tradução, quando a saída pede; nula enquanto não traduzida.</param>
/// <param name="Palavras">Palavras com tempo, na ordem.</param>
/// <param name="Sentido">O que a pessoa quis dizer (intenção, tom), no idioma da tradução; nulo sem análise.</param>
/// <param name="Blocos">Unidades de sentido (expressões, phrasal verbs...) apontando para as palavras; nulo sem análise.</param>
public sealed record Frase(
    double Inicio,
    double Fim,
    string Texto,
    string? Traducao,
    IReadOnlyList<Palavra> Palavras,
    string? Sentido = null,
    IReadOnlyList<Bloco>? Blocos = null);

/// <summary>Palavra com tempos em segundos.</summary>
/// <param name="Texto">Palavra como foi reconhecida (com a pontuação colada).</param>
/// <param name="Inicio">Início, em segundos.</param>
/// <param name="Fim">Fim, em segundos.</param>
/// <param name="Confianca">
/// Probabilidade de 0 a 1 que o reconhecedor deu à palavra (baixa = provável erro de reconhecimento); nula quando o
/// motor não informa (tempo real) ou em JSON gravado antes deste campo existir.
/// </param>
public sealed record Palavra(string Texto, double Inicio, double Fim, double? Confianca = null);

public enum TipoBloco
{
    /// <summary>Trecho comum que se traduz junto.</summary>
    Literal,

    /// <summary>Expressão idiomática ("call it a day").</summary>
    Expressao,

    /// <summary>Phrasal verb ("give up").</summary>
    Phrasal,

    /// <summary>Colocação: palavras que andam juntas e mudam de sentido ("evil forebodings").</summary>
    Colocacao,

    /// <summary>Nome próprio ("St. Petersburg").</summary>
    Nome,
}

/// <summary>Unidade de sentido de uma frase.</summary>
/// <param name="PalavraInicial">Índice (em <see cref="Frase.Palavras"/>) da primeira palavra do bloco.</param>
/// <param name="PalavraFinal">Índice da última palavra do bloco (inclusive).</param>
/// <param name="Texto">As palavras do bloco, no idioma falado.</param>
/// <param name="Traducao">
/// Como o bloco foi traduzido. Quando é um trecho literal de <see cref="Frase.Traducao"/>, o app que consome o resultado pode destacá-lo na tradução.
/// </param>
/// <param name="Tipo">Tipo do bloco.</param>
/// <param name="Nota">Explicação curta quando o sentido literal engana; nula se não há o que explicar.</param>
public sealed record Bloco(int PalavraInicial, int PalavraFinal, string Texto, string Traducao, TipoBloco Tipo, string? Nota);
