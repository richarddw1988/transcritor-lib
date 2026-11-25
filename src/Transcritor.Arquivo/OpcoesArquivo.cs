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

    /// <summary>
    /// Sem <see cref="Idioma"/> forçado, confere a detecção do Whisper entre português e inglês: transcreve o começo
    /// do áudio (<see cref="SegundosConferirIdioma"/>) também no outro idioma e fica com o que saiu com mais confiança.
    /// Evita a fala com sotaque voltar já traduzida para o idioma errado. Custa uma transcrição curta a mais por arquivo
    /// (e outra do começo, nos arquivos mais longos que a amostra).
    /// </summary>
    public bool ConferirIdioma { get; set; } = true;

    /// <summary>Tamanho da amostra da conferência do idioma (o Whisper também detecta pelos primeiros 30 s).</summary>
    public double SegundosConferirIdioma { get; set; } = 30;

    /// <summary>
    /// Nomes e termos do assunto (pessoas, lugares, siglas, jargão), um por linha ou separados por vírgula, mandados ao
    /// Whisper como dica em todos os arquivos. Soma com o arquivo ao lado da mídia
    /// (<see cref="LerVocabularioAoLado"/>). Até 800 caracteres; o que passar fica de fora. Nulo = sem dica.
    /// </summary>
    public string? Vocabulario { get; set; }

    /// <summary>
    /// Lê também "&lt;arquivo&gt;.vocabulario.txt" ao lado da mídia, se existir: o vocabulário daquele vídeo.
    /// </summary>
    public bool LerVocabularioAoLado { get; set; } = true;

    /// <summary>Executável do ffmpeg (nome no PATH ou caminho completo).</summary>
    public string CaminhoFfmpeg { get; set; } = "ffmpeg";

    /// <summary>Executável do ffprobe (nome no PATH ou caminho completo).</summary>
    public string CaminhoFfprobe { get; set; } = "ffprobe";

    /// <summary>Pasta do cache de resultados, indexado pelo hash do arquivo.</summary>
    public string PastaCache { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "cache");

    /// <summary>Também grava "&lt;arquivo&gt;.transcricao.json" ao lado da mídia.</summary>
    public bool GravarJsonAoLado { get; set; } = true;

    /// <summary>
    /// Transcreve de novo, isolado, cada trecho em que o Whisper devolveu palavras sem som alinhado (duração zero),
    /// sinal de alucinação ou repetição na transcrição longa.
    /// </summary>
    public bool RepararTrechosSuspeitos { get; set; } = true;

    /// <summary>Limite de trechos refeitos por arquivo (cada um é uma chamada a mais ao Speaches).</summary>
    public int MaximoTrechosReparados { get; set; } = 30;

    /// <summary>
    /// Transcreve o áudio em pedaços deste tamanho, cortados no ponto mais silencioso perto de cada marca. Cada pedaço
    /// leva como dica só a última frase do anterior (e o vocabulário). No arquivo inteiro, o Whisper entrava em loop
    /// ("no, no, no…") e pulava janelas de 30 s que começavam com música, levando a fala junto: num filme de 2 h, o
    /// erro caiu de 55% para ~16% com pedaços de 30 s (docs/modelos.md). Áudio de até 1,5 pedaço vai inteiro.
    /// 0 = arquivo inteiro de uma vez.
    /// </summary>
    public double SegundosPorPedaco { get; set; } = 30;

    /// <summary>
    /// Com a correção da transcrição ligada, confere cada troca no áudio: reouve a frase com a versão trocada como
    /// dica e só aceita se o Whisper ouvir a palavra nova (veja <see cref="LimiteConfiancaReouvir"/>).
    /// </summary>
    public bool ConferirCorrecaoNoAudio { get; set; } = true;

    /// <summary>Confiança mínima da palavra nova na frase reouvida para a troca valer.</summary>
    public double LimiteConfiancaReouvir { get; set; } = 0.2;

    /// <summary>
    /// Ao fim de cada etapa (transcrição, propostas da correção, conferência no áudio, tradução, análise), tira da GPU o
    /// modelo usado antes de carregar o da etapa seguinte; antes de cada etapa, tira também o do outro serviço (Whisper ou
    /// Ollama) que tenha sobrado de outra execução. Assim cada um cabe inteiro na placa (com 12 GB, o Whisper e um
    /// modelo de 8 GB do Ollama ficam a poucos MB do limite). Serviços que não implementam
    /// <see cref="Core.Contratos.IDescarregavel"/> são pulados.
    /// </summary>
    public bool DescarregarModelosEntreEtapas { get; set; } = true;

    /// <summary>
    /// Quanto esperar o Whisper sair da GPU ao fim da etapa, depois de pedir. Com a imagem original do Speaches 0.8, que
    /// não aceita o pedido, é o tempo máximo de espera pelo tempo ocioso do serviço (<c>WHISPER__TTL</c>); com um TTL
    /// maior, a etapa seguinte começa com o Whisper ainda carregado.
    /// </summary>
    public TimeSpan EsperaDescarregarTranscricao { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Intervalo entre as consultas ao Speaches enquanto espera o Whisper sair.</summary>
    internal TimeSpan IntervaloConsultaDescarregar { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Tempo máximo de uma chamada de transcrição.</summary>
    public TimeSpan TimeoutTranscricao { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Caminho do JSON gravado ao lado da mídia.</summary>
    public static string CaminhoJsonAoLado(string caminhoArquivo) => caminhoArquivo + ".transcricao.json";

    /// <summary>Caminho do vocabulário lido ao lado da mídia (ex.: "aula.mp4.vocabulario.txt").</summary>
    public static string CaminhoVocabularioAoLado(string caminhoArquivo) => caminhoArquivo + ".vocabulario.txt";
}
