namespace Transcritor.TempoReal;

/// <summary>Configuração do tempo real. Seção sugerida: "Transcritor:TempoReal".</summary>
public sealed class OpcoesTempoReal
{
    /// <summary>Endpoint WebSocket do WhisperLiveKit.</summary>
    public Uri WhisperLiveKitUrl { get; set; } = new("ws://localhost:8001/asr");

    /// <summary>Tempo máximo para conectar.</summary>
    public TimeSpan TimeoutConexao { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Depois que o áudio termina, quanto esperar o servidor entregar o resto ("ready_to_stop")
    /// e as últimas traduções.
    /// </summary>
    public TimeSpan TimeoutFinalizacao { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Quantos trechos confirmados anteriores vão como contexto da tradução.</summary>
    public int TrechosContexto { get; set; } = 2;

    /// <summary>
    /// A sessão pega a vez na GPU da máquina (trava em <see cref="ArquivoTravaGpu"/>) com prioridade: o processamento de
    /// arquivo espera a sessão fechar antes da próxima etapa. Se um arquivo estiver no meio de uma etapa, a sessão espera
    /// a etapa acabar (evento <see cref="Core.Contratos.TipoEvento.Aguardando"/>).
    /// </summary>
    public bool CoordenarGpu { get; set; } = true;

    /// <summary>Arquivo da vez na GPU; o mesmo do processamento de arquivo.</summary>
    public string ArquivoTravaGpu { get; set; } = Core.Gpu.VezGpu.CaminhoPadrao;

    /// <summary>
    /// Liga o contêiner do WhisperLiveKit (<see cref="ConteinerTempoReal"/>) quando uma sessão abre e ele está parado, e o
    /// desliga depois de <see cref="DesligarAposOcioso"/> sem sessão.
    /// </summary>
    public bool GerenciarConteiner { get; set; } = true;

    /// <summary>Nome do contêiner do WhisperLiveKit.</summary>
    public string ConteinerTempoReal { get; set; } = "whisperlivekit";

    /// <summary>Executável do docker (nome no PATH ou caminho completo).</summary>
    public string CaminhoDocker { get; set; } = "docker";

    /// <summary>Quanto esperar o WhisperLiveKit ligado aceitar conexão (ele carrega o modelo ao subir).</summary>
    public TimeSpan TempoMaximoSubida { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Depois que a última sessão fecha, quanto tempo o WhisperLiveKit fica no ar antes de ser desligado (abrir outra
    /// sessão logo em seguida não paga a subida). Nulo: não desliga. Ao encerrar o programa (descartar os serviços), um
    /// desligamento pendente é feito na hora.
    /// </summary>
    public TimeSpan? DesligarAposOcioso { get; set; } = TimeSpan.FromMinutes(5);
}
