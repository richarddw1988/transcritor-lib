namespace Transcritor.Core.Processos;

/// <summary>
/// Liga e desliga sob demanda os contêineres dos serviços de arquivo e tradução (Speaches, separador de voz, Ollama).
/// Seção sugerida: "Transcritor:Conteineres". O WhisperLiveKit tem regra própria (<c>OpcoesTempoReal</c>).
/// </summary>
public sealed class OpcoesConteineres
{
    /// <summary>
    /// Liga o contêiner parado na primeira chamada ao serviço e o desliga depois de <see cref="DesligarAposOcioso"/> sem
    /// uso. Desligado: a lib só chama os serviços, que precisam estar no ar.
    /// </summary>
    public bool Gerenciar { get; set; } = true;

    /// <summary>Executável do docker (nome no PATH ou caminho completo).</summary>
    public string CaminhoDocker { get; set; } = "docker";

    /// <summary>Quanto esperar o contêiner ligado responder.</summary>
    public TimeSpan TempoMaximoSubida { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Sem uso por este tempo (de nenhum programa que use a lib), o contêiner é desligado. Nulo: não desliga. Ao encerrar
    /// o programa (descartar os serviços), o que ele usou e ninguém mais está usando é desligado na hora.
    /// </summary>
    public TimeSpan? DesligarAposOcioso { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Pasta das marcas de uso, que avisam os outros programas da máquina quem está usando cada contêiner.</summary>
    public string PastaUso { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcritor", "conteineres");
}
