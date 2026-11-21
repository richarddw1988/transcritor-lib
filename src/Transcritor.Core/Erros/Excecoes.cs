namespace Transcritor.Core.Erros;

/// <summary>Base dos erros da lib.</summary>
public class TranscritorException : Exception
{
    public TranscritorException() { }
    public TranscritorException(string message) : base(message) { }
    public TranscritorException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Um serviço local (Speaches, WhisperLiveKit, Ollama) não respondeu.</summary>
public class ServicoIndisponivelException : TranscritorException
{
    public ServicoIndisponivelException() { }
    public ServicoIndisponivelException(string message) : base(message) { }
    public ServicoIndisponivelException(string message, Exception? innerException) : base(message, innerException) { }

    public ServicoIndisponivelException(string servico, Uri url, Exception? innerException)
        : base($"O serviço {servico} não respondeu em {url}. Verifique se o contêiner está no ar (docker compose up -d {servico.ToLowerInvariant()}).", innerException)
    {
        Servico = servico;
        Url = url;
    }

    public string? Servico { get; }
    public Uri? Url { get; }
}

/// <summary>O serviço respondeu, mas o modelo pedido não está instalado nele.</summary>
public class ModeloNaoInstaladoException : TranscritorException
{
    public ModeloNaoInstaladoException() { }
    public ModeloNaoInstaladoException(string message) : base(message) { }
    public ModeloNaoInstaladoException(string message, Exception? innerException) : base(message, innerException) { }

    public ModeloNaoInstaladoException(string servico, string modelo, string comoInstalar)
        : base($"O modelo '{modelo}' não está instalado no {servico}. Para instalar: {comoInstalar}")
    {
        Servico = servico;
        Modelo = modelo;
    }

    public string? Servico { get; }
    public string? Modelo { get; }
}

/// <summary>Uma ferramenta externa (ffmpeg, ffprobe) falhou ou não foi encontrada.</summary>
public class FerramentaExternaException : TranscritorException
{
    public FerramentaExternaException() { }
    public FerramentaExternaException(string message) : base(message) { }
    public FerramentaExternaException(string message, Exception? innerException) : base(message, innerException) { }
}
