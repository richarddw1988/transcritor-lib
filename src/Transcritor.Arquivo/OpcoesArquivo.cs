namespace Transcritor.Arquivo;

/// <summary>Configuração do módulo de arquivo. Seção sugerida: "Transcritor:Arquivo".</summary>
public sealed class OpcoesArquivo
{
    /// <summary>URL base do Speaches.</summary>
    public Uri SpeachesUrl { get; set; } = new("http://localhost:8000");

    /// <summary>Modelo Whisper instalado no Speaches.</summary>
    public string ModeloTranscricao { get; set; } = "Systran/faster-whisper-large-v3";

    /// <summary>Força o idioma falado ("pt" ou "en"). Nulo = detecção automática.</summary>
    public string? Idioma { get; set; }

    /// <summary>Executável do ffmpeg (nome no PATH ou caminho completo).</summary>
    public string CaminhoFfmpeg { get; set; } = "ffmpeg";

    /// <summary>Executável do ffprobe (nome no PATH ou caminho completo).</summary>
    public string CaminhoFfprobe { get; set; } = "ffprobe";

    /// <summary>Pasta do cache de resultados, indexado pelo hash do arquivo.</summary>
    public string PastaCache { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "cache");

    /// <summary>Também grava "&lt;arquivo&gt;.transcricao.json" ao lado da mídia.</summary>
    public bool GravarJsonAoLado { get; set; } = true;

    /// <summary>Tempo máximo de uma chamada de transcrição.</summary>
    public TimeSpan TimeoutTranscricao { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Caminho do JSON gravado ao lado da mídia.</summary>
    public static string CaminhoJsonAoLado(string caminhoArquivo) => caminhoArquivo + ".transcricao.json";
}
