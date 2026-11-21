namespace Transcritor.Traducao;

/// <summary>Configuração da tradução. Seção sugerida: "Transcritor:Traducao".</summary>
public sealed class OpcoesTraducao
{
    /// <summary>URL base do Ollama.</summary>
    public Uri OllamaUrl { get; set; } = new("http://localhost:11434");

    /// <summary>
    /// Modelo para as frases de um arquivo (processado antes de assistir; qualidade vale mais que velocidade).
    /// No audiolivro de teste o 12b corrigiu erros de gramática e sentido do 4b, a ~6,7 s por frase.
    /// </summary>
    public string ModeloFrases { get; set; } = "translategemma:12b";

    /// <summary>
    /// Modelo para frase avulsa: tempo real, comando traduzir e a frase de apoio da palavra (~1–2 s por frase).
    /// Vazio = usa <see cref="ModeloFrases"/>.
    /// </summary>
    public string ModeloFrasesTempoReal { get; set; } = "translategemma:4b";

    /// <summary>Modelo para palavra solta no contexto da frase.</summary>
    public string ModeloPalavras { get; set; } = "gemma3:4b";

    /// <summary>
    /// Liga a análise de sentido no processamento de arquivos (blocos e "o que quis dizer" em cada frase).
    /// Desligada por padrão: com o gemma3:12b custa ~13 s por frase.
    /// </summary>
    public bool AnalisarSentido { get; set; }

    /// <summary>Modelo para unidades de sentido e "o que quis dizer" (análise do arquivo).</summary>
    public string ModeloAnalise { get; set; } = "gemma3:12b";

    /// <summary>Quantas frases anteriores vão como contexto.</summary>
    public int FrasesAntes { get; set; } = 2;

    /// <summary>Quantas frases seguintes vão como contexto.</summary>
    public int FrasesDepois { get; set; } = 2;

    /// <summary>Quanto tempo o Ollama mantém o modelo na memória depois do uso (formato do Ollama: "10m", "0", "-1").</summary>
    public string KeepAlive { get; set; } = "10m";

    /// <summary>Tempo máximo de uma chamada (inclui carregar o modelo na 1ª vez).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// SQLite do dicionário inglês ↔ português (gerado por <c>transcritor dicionario importar</c>). Quando existe, a
    /// tradução de palavra recebe os sentidos do dicionário como opções numeradas e o modelo escolhe uma.
    /// Nulo ou arquivo ausente = tradução de palavra só com o modelo.
    /// </summary>
    public string? CaminhoDicionario { get; set; }

    /// <summary>Arquivo do cache de palavras.</summary>
    public string CaminhoCachePalavras { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "cache", "palavras.json");
}
