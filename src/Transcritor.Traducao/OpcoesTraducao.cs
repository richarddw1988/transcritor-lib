namespace Transcritor.Traducao;

/// <summary>Configuração da tradução. Seção sugerida: "Transcritor:Traducao".</summary>
public sealed class OpcoesTraducao
{
    /// <summary>URL base do Ollama.</summary>
    public Uri OllamaUrl { get; set; } = new("http://localhost:11434");

    /// <summary>Modelo para frases (arquivo e tempo real).</summary>
    public string ModeloFrases { get; set; } = "translategemma:4b";

    /// <summary>Modelo para palavra solta no contexto da frase.</summary>
    public string ModeloPalavras { get; set; } = "gemma3:4b";

    /// <summary>Quantas frases anteriores vão como contexto.</summary>
    public int FrasesAntes { get; set; } = 2;

    /// <summary>Quantas frases seguintes vão como contexto.</summary>
    public int FrasesDepois { get; set; } = 2;

    /// <summary>Quanto tempo o Ollama mantém o modelo na memória depois do uso (formato do Ollama: "10m", "0", "-1").</summary>
    public string KeepAlive { get; set; } = "10m";

    /// <summary>Tempo máximo de uma chamada (inclui carregar o modelo na 1ª vez).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Arquivo do cache de palavras.</summary>
    public string CaminhoCachePalavras { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "cache", "palavras.json");
}
