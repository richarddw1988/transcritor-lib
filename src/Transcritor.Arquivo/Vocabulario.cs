using System.Security.Cryptography;
using System.Text;

namespace Transcritor.Arquivo;

/// <summary>
/// Vocabulário do assunto (nomes de pessoas, lugares, termos técnicos) mandado ao Whisper como dica ("prompt").
/// Medido no FLEURS (docs/modelos.md, 2026-11-24): os termos certos foram de 86% para 97% em português e de 81% para
/// 95% em inglês.
/// </summary>
internal static class Vocabulario
{
    /// <summary>
    /// Limite do texto mandado. O Whisper aceita ~224 tokens de dica e o faster-whisper descarta o começo do que
    /// passar; 800 caracteres ficam dentro disso. Termos que passarem do limite ficam de fora inteiros.
    /// </summary>
    internal const int MaximoCaracteres = 800;

    private static readonly char[] Separadores = ['\n', '\r', ',', ';'];

    /// <summary>
    /// Junta os textos (um termo por linha ou separados por vírgula ou ponto e vírgula), tira repetidos e linhas de
    /// comentário (#) e devolve "termo1, termo2, termo3." — o formato medido. Nulo quando não sobra termo.
    /// </summary>
    internal static string? Montar(params string?[] textos)
    {
        var termos = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texto in textos)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                continue;
            }

            foreach (var linha in texto.Split('\n'))
            {
                if (linha.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                foreach (var parte in linha.Split(Separadores, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var termo = parte.TrimEnd('.');
                    if (termo.Length > 0 && vistos.Add(termo))
                    {
                        termos.Add(termo);
                    }
                }
            }
        }

        var resultado = new StringBuilder();
        foreach (var termo in termos)
        {
            var extra = (resultado.Length > 0 ? 2 : 0) + termo.Length;
            if (resultado.Length + extra + 1 > MaximoCaracteres)
            {
                break;
            }

            if (resultado.Length > 0)
            {
                resultado.Append(", ");
            }

            resultado.Append(termo);
        }

        return resultado.Length == 0 ? null : resultado.Append('.').ToString();
    }

    /// <summary>Lê o vocabulário ao lado da mídia (<see cref="OpcoesArquivo.CaminhoVocabularioAoLado"/>), se existir.</summary>
    internal static async Task<string?> LerAoLadoAsync(string caminhoMidia, CancellationToken cancellationToken)
    {
        var caminho = OpcoesArquivo.CaminhoVocabularioAoLado(caminhoMidia);
        return File.Exists(caminho) ? await File.ReadAllTextAsync(caminho, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Trecho da chave do cache: muda quando o vocabulário muda.</summary>
    internal static string Chave(string vocabulario) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(vocabulario)))[..8];

    /// <summary>Dica do trecho refeito: o vocabulário antes e o texto falado logo antes do trecho no fim.</summary>
    internal static string? ComContexto(string? vocabulario, string? contexto) =>
        string.IsNullOrWhiteSpace(vocabulario) ? contexto
        : string.IsNullOrWhiteSpace(contexto) ? vocabulario
        : $"{vocabulario} {contexto}";
}
