using Transcritor.Core;
using Transcritor.Core.Contratos;

namespace Transcritor.TempoReal.WhisperLiveKit;

/// <summary>
/// Transforma o estado completo que o WhisperLiveKit manda a cada atualização em eventos.
/// <list type="bullet">
/// <item>As linhas do servidor são separadas por pausas; a última linha cresce enquanto a pessoa fala.
/// O texto de uma linha já é definitivo, só ganha continuação.</item>
/// <item>Confirmado: cada frase fechada por pontuação final dentro de uma linha, e o resto da linha quando
/// ela termina (veio uma pausa ou outra linha depois, ou a sessão acabou).</item>
/// <item>Provisorio: o resto da linha aberta + buffer_transcription (texto que o modelo ainda pode mudar).</item>
/// </list>
/// Tempos de frases no meio de uma linha são estimados pela posição do texto na linha.
/// </summary>
internal sealed class DerivadorEventos(string? idiomaForcado)
{
    private static readonly char[] PontuacaoFinal = ['.', '?', '!', '…', '。', '！', '？'];

    private readonly string? _idiomaForcado = string.IsNullOrWhiteSpace(idiomaForcado) ? null : Idiomas.Normalizar(idiomaForcado);

    private int _linhasFechadas;
    private int _consumidosNaLinhaAberta;
    private long _proximoId = 1;
    private string _ultimoProvisorio = string.Empty;
    private MensagemWlk? _ultimaMensagem;

    public IReadOnlyList<EventoTempoReal> Processar(MensagemWlk mensagem)
    {
        _ultimaMensagem = mensagem;
        return Derivar(mensagem, sessaoTerminou: false);
    }

    /// <summary>Confirma tudo o que estiver pendente (fim da sessão).</summary>
    public IReadOnlyList<EventoTempoReal> Finalizar() =>
        _ultimaMensagem is null ? [] : Derivar(_ultimaMensagem with { BufferTranscricao = string.Empty }, sessaoTerminou: true);

    private List<EventoTempoReal> Derivar(MensagemWlk mensagem, bool sessaoTerminou)
    {
        var eventos = new List<EventoTempoReal>();
        var fala = mensagem.Linhas.Where(l => !l.EhSilencio).ToList();
        var terminaEmSilencio = mensagem.Linhas.Count > 0 && mensagem.Linhas[^1].EhSilencio;

        var pendente = string.Empty;
        LinhaWlk? linhaAberta = null;
        var inicioPendente = 0d;

        for (var i = _linhasFechadas; i < fala.Count; i++)
        {
            var linha = fala[i];
            var texto = linha.Texto ?? string.Empty;
            var fechada = i < fala.Count - 1 || terminaEmSilencio || sessaoTerminou;

            var consumidos = i == _linhasFechadas ? Math.Min(_consumidosNaLinhaAberta, texto.Length) : 0;
            foreach (var (inicio, fim) in FrasesCompletas(texto, consumidos))
            {
                Confirmar(eventos, linha, texto, inicio, fim);
                consumidos = fim;
            }

            if (fechada)
            {
                if (texto[consumidos..].Trim().Length > 0)
                {
                    Confirmar(eventos, linha, texto, consumidos, texto.Length);
                }

                _linhasFechadas = i + 1;
                _consumidosNaLinhaAberta = 0;
                continue;
            }

            _consumidosNaLinhaAberta = consumidos;
            pendente = texto[consumidos..].Trim();
            linhaAberta = linha;
            inicioPendente = Estimar(linha, texto, consumidos);
            break;
        }

        var provisorio = string.Join(' ', new[] { pendente, mensagem.BufferTranscricao.Trim() }.Where(t => t.Length > 0));
        if (provisorio != _ultimoProvisorio)
        {
            _ultimoProvisorio = provisorio;
            var fimProvisorio = linhaAberta?.Fim ?? fala.LastOrDefault()?.Fim ?? 0;
            eventos.Add(new EventoTempoReal(
                TipoEvento.Provisorio,
                _proximoId,
                provisorio,
                null,
                Idioma(linhaAberta ?? fala.LastOrDefault()),
                linhaAberta is null ? fimProvisorio : inicioPendente,
                fimProvisorio));
        }

        return eventos;
    }

    private void Confirmar(List<EventoTempoReal> eventos, LinhaWlk linha, string textoLinha, int inicio, int fim)
    {
        var trecho = textoLinha[inicio..fim].Trim();
        if (trecho.Length == 0)
        {
            return;
        }

        eventos.Add(new EventoTempoReal(
            TipoEvento.Confirmado,
            _proximoId++,
            trecho,
            null,
            Idioma(linha),
            Estimar(linha, textoLinha, inicio),
            Estimar(linha, textoLinha, fim)));
    }

    /// <summary>Frases completas (terminadas por pontuação final seguida de espaço ou fim do texto) a partir de <paramref name="desde"/>.</summary>
    private static IEnumerable<(int Inicio, int Fim)> FrasesCompletas(string texto, int desde)
    {
        var inicio = desde;
        for (var i = desde; i < texto.Length; i++)
        {
            if (Array.IndexOf(PontuacaoFinal, texto[i]) < 0)
            {
                continue;
            }

            var fim = i + 1;
            while (fim < texto.Length && Array.IndexOf(PontuacaoFinal, texto[fim]) >= 0)
            {
                fim++;
            }

            if (fim < texto.Length && !char.IsWhiteSpace(texto[fim]) && texto[fim] is not ('"' or '\'' or ')' or '”' or '’'))
            {
                i = fim - 1;
                continue; // "3.5", "e.g" etc.
            }

            if (texto[inicio..fim].Trim().Length > 0)
            {
                yield return (inicio, fim);
            }

            inicio = fim;
            i = fim - 1;
        }
    }

    private static double Estimar(LinhaWlk linha, string texto, int posicao)
    {
        if (texto.Length == 0)
        {
            return linha.Inicio;
        }

        var duracao = Math.Max(0, linha.Fim - linha.Inicio);
        return Math.Round(linha.Inicio + (duracao * posicao / texto.Length), 2);
    }

    private string? Idioma(LinhaWlk? linha) =>
        _idiomaForcado ?? (string.IsNullOrWhiteSpace(linha?.Idioma) ? null : Idiomas.Normalizar(linha.Idioma));
}
