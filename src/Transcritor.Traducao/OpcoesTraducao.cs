namespace Transcritor.Traducao;

/// <summary>Configuração da tradução. Seção sugerida: "Transcritor:Traducao".</summary>
public sealed class OpcoesTraducao
{
    /// <summary>URL base do Ollama.</summary>
    public Uri OllamaUrl { get; set; } = new("http://localhost:11434");

    /// <summary>
    /// Contêiner do Ollama, ligado na primeira tradução, correção ou análise e desligado depois de um tempo sem uso
    /// (<see cref="Core.Processos.OpcoesConteineres"/>). Vazio (ex.: Ollama instalado fora do docker): a lib só chama.
    /// </summary>
    public string ConteinerOllama { get; set; } = "ollama";

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

    /// <summary>
    /// Liga a correção de palavras ouvidas errado no processamento de arquivos (antes de traduzir): o modelo troca uma
    /// palavra por outra de som parecido quando a frase não faz sentido ("night" → "knight"). Cada troca é conferida
    /// pelo BERT (<see cref="ConferirCorrecaoComBert"/>) e no áudio. Desligada por padrão desde 2026-11-24: no FLEURS,
    /// no CORAA e no AMI o WER não mudou (na fala espontânea do CORAA estragou 2 palavras certas e acertou 1) e custou de
    /// 9 a 33 s por frase (docs/modelos.md). Precisa do <see cref="ModeloCorrecao"/> no Ollama.
    /// </summary>
    public bool CorrigirTranscricao { get; set; }

    /// <summary>Modelo da correção de palavras ouvidas errado.</summary>
    public string ModeloCorrecao { get; set; } = "gemma3:12b";

    /// <summary>
    /// Só palavras com confiança do reconhecimento abaixo disto podem ser trocadas; frase sem nenhuma não vai ao modelo.
    /// Palavra sem confiança informada (tempo real) sempre pode.
    /// </summary>
    public double LimiteConfiancaCorrecao { get; set; } = 0.95;

    /// <summary>
    /// Cada palavra que tem homófono no dicionário ("cela" ↔ "sela", "bare" ↔ "bear") passa por um BERT
    /// (modelo de lacuna) que dá a probabilidade de cada grafia pelo sentido da frase; troca se outra grafia passar de
    /// <see cref="LimiteHomofono"/>, mesmo que o reconhecimento estivesse seguro. Precisa do dicionário gerado com a
    /// lista de homófonos (desde 2026-11-23) e dos modelos em <see cref="PastaModelosHomofonos"/>; sem eles, não faz nada.
    /// Roda mesmo com <see cref="CorrigirTranscricao"/> desligada (no arquivo, na etapa da correção).
    /// </summary>
    public bool CorrigirHomofonos { get; set; } = true;

    /// <summary>
    /// Pasta dos BERT convertidos para ONNX (<c>&lt;pasta&gt;/en</c> e <c>&lt;pasta&gt;/pt</c>, cada uma com model.onnx e
    /// vocab.txt; gerados por <c>scripts/preparar-ambiente.ps1</c>). Nulo = sem escolha de homófonos.
    /// </summary>
    public string? PastaModelosHomofonos { get; set; }

    /// <summary>Probabilidade mínima da outra grafia para trocar o homófono (abaixo de 0,99 entraram trocas erradas).</summary>
    public double LimiteHomofono { get; set; } = 0.99;

    /// <summary>
    /// Homófono em que o BERT dá à outra grafia pelo menos <see cref="LimiteHomofonoComModelo"/>, mas não chega a
    /// <see cref="LimiteHomofono"/>, vai ao <see cref="ModeloCorrecao"/>: ele vê a frase com cada grafia e diz qual está
    /// escrita certo, duas vezes, com as opções em ordem trocada. Só troca se escolher a outra grafia nas duas (o modelo
    /// de lacuna não sabe que se diz "concerto de música"; o de conversa sabe). Mais acertos nos casos difíceis, mas nas
    /// 49 gravações do usuário só levou o WER de 6,2% a 6,0%; desligada por padrão desde 2026-11-24, junto com
    /// <see cref="CorrigirTranscricao"/>, para o padrão não precisar do <see cref="ModeloCorrecao"/> (8 GB). Em texto
    /// coloquial ou literário ainda troca a mais às vezes ("pra trás" → "traz"; docs/modelos.md).
    /// </summary>
    public bool ConfirmarHomofonoComModelo { get; set; }

    /// <summary>Probabilidade mínima, pelo BERT, da outra grafia para o homófono ir ao modelo de conversa.</summary>
    public double LimiteHomofonoComModelo { get; set; } = 0.02;

    /// <summary>
    /// Cada troca proposta pelo <see cref="ModeloCorrecao"/> só vale se o BERT dos homófonos der à palavra nova pelo menos
    /// <see cref="LimiteParecida"/> de probabilidade contra a ouvida. Sem os modelos BERT, não confere. Medido: barrou a
    /// única troca errada da fala do usuário ("leave" → "live") sem perder nenhuma certa.
    /// </summary>
    public bool ConferirCorrecaoComBert { get; set; } = true;

    /// <summary>
    /// Probabilidade mínima, pelo BERT, da palavra nova contra a ouvida numa troca do modelo (com 0,7 já perdeu uma troca
    /// certa).
    /// </summary>
    public double LimiteParecida { get; set; } = 0.5;

    /// <summary>
    /// Cada troca proposta vai a uma segunda chamada ao modelo, que compara a frase com e sem ela; a troca só vale se a
    /// frase sem ela não fizer sentido. Desligada por padrão porque no arquivo a troca já é conferida no áudio
    /// (<c>OpcoesArquivo.ConferirCorrecaoNoAudio</c>), que mediu melhor; ligue ao corrigir frase avulsa, sem áudio.
    /// </summary>
    public bool ConfirmarCorrecaoComModelo { get; set; }

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
    /// palavra que faz parte de uma expressão do dicionário ("gave" em "gave up") recebe os sentidos da expressão como
    /// opções numeradas e o modelo escolhe uma.
    /// Se o dicionário foi gerado com as listas de frequência e <see cref="LimiarPalavraRara"/> está ligado, as frases do
    /// arquivo também recebem os sentidos das palavras raras.
    /// Nulo ou arquivo ausente = tradução de palavra só com o modelo.
    /// </summary>
    public string? CaminhoDicionario { get; set; }

    /// <summary>
    /// Experimental, desligado por padrão (0). Na tradução das frases do arquivo, palavra fora das N mais frequentes da
    /// lista de frequência é rara e vai com os sentidos do dicionário nas instruções do modelo (medido com 10000:
    /// acerta vocabulário raro, mas às vezes muda o sentido de outra parte da frase; ver docs/modelos.md). Precisa do
    /// dicionário gerado com as listas de frequência; a frase avulsa (tempo real) não usa.
    /// </summary>
    public int LimiarPalavraRara { get; set; }

    /// <summary>
    /// Termos com tradução fixa (nomes, títulos, termos técnicos), usados nas frases e na palavra solta.
    /// Na configuração: <c>"Glossario": [ { "Origem": "en", "Termo": "Giant Peach", "Traducao": "Pêssego Gigante" } ]</c>.
    /// </summary>
    public IList<EntradaGlossario> Glossario { get; } = [];

    /// <summary>Arquivo do cache de palavras.</summary>
    public string CaminhoCachePalavras { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "cache", "palavras.json");
}
