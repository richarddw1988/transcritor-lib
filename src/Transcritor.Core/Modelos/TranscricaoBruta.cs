namespace Transcritor.Core.Modelos;

/// <summary>Saída de um motor de transcrição, antes de montar as frases.</summary>
/// <param name="Idioma">Idioma detectado (ISO 639-1).</param>
/// <param name="Duracao">Duração do áudio em segundos.</param>
/// <param name="Segmentos">Trechos devolvidos pelo motor, em ordem.</param>
/// <param name="Palavras">Palavras com tempo, em ordem. Pode vir vazio se o motor não der tempo por palavra.</param>
public sealed record TranscricaoBruta(
    string Idioma,
    double Duracao,
    IReadOnlyList<SegmentoBruto> Segmentos,
    IReadOnlyList<Palavra> Palavras);

/// <summary>Trecho devolvido pelo motor.</summary>
/// <param name="Inicio">Início em segundos.</param>
/// <param name="Fim">Fim em segundos.</param>
/// <param name="Texto">Texto do trecho.</param>
/// <param name="SemFala">Chance de o trecho ser silêncio segundo o motor (no_speech_prob do Whisper); nulo se não informa.</param>
public sealed record SegmentoBruto(double Inicio, double Fim, string Texto, double? SemFala = null);
