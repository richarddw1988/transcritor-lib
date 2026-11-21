using Transcritor.Core;
using Transcritor.Core.Contratos;

namespace Transcritor.TempoReal.WhisperLiveKit;

/// <summary>
/// Transforma o estado completo que o WhisperLiveKit manda a cada atualização em eventos.
/// <list type="bullet">
/// <item>As linhas do servidor são separadas por pausas; a última linha cresce enquanto a pessoa fala.
/// O texto de uma linha já é definitivo, só ganha continuação.</item>
/// <item>O texto de uma linha é a junção crua dos tokens; palavra nova começa com espaço. Uma linha que
/// começa sem espaço continua a palavra da anterior (a pausa caiu no meio de "chu|va"): as duas viram uma só.</item>
/// <item>Confirmado: cada frase fechada por pontuação final, e o resto da linha quando ela termina (outra linha
/// começou com palavra nova, a pausa passou de <see cref="PausaQueFechaPalavra"/> com a linha terminando no
/// meio de uma palavra, ou a sessão acabou).</item>
/// <item>Provisorio: o resto da linha aberta + buffer_transcription (texto que o modelo ainda pode mudar).</item>
/// </list>
/// Tempos de frases no meio de uma linha são estimados pela posição do texto na linha.
/// </summary>
internal sealed class DerivadorEventos(string? idiomaForcado)
{
    /// <summary>Pausa depois da qual uma linha que termina no meio de uma palavra é confirmada mesmo assim.</summary>
    internal static readonly TimeSpan PausaQueFechaPalavra = TimeSpan.FromSeconds(1.5);

    private static readonly char[] PontuacaoFinal = ['.', '?', '!', '…', '。', '！', '？'];

    private readonly string? _idiomaForcado = string.IsNullOrWhiteSpace(idiomaForcado) ? null : Idiomas.Normalizar(idiomaForcado);

    /// <summary>Quantas linhas de fala do servidor (não as lógicas) já foram totalmente confirmadas.</summary>
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

    /// <summary>Linha lógica: uma ou mais linhas do servidor coladas porque a pausa cortou uma palavra.</summary>
    private sealed record LinhaLogica(string Texto, double Inicio, double Fim, string? Idioma, int LinhasFisicas);

    private List<EventoTempoReal> Derivar(MensagemWlk mensagem, bool sessaoTerminou)
    {
        var eventos = new List<EventoTempoReal>();
        var fala = mensagem.Linhas.Where(l => !l.EhSilencio).ToList();
        var silencioFinal = mensagem.Linhas.Count > 0 && mensagem.Linhas[^1].EhSilencio ? mensagem.Linhas[^1] : null;

        var logicas = MontarLinhasLogicas(fala, _linhasFechadas);

        var pendente = string.Empty;
        LinhaLogica? linhaAberta = null;
        var inicioPendente = 0d;

        for (var i = 0; i < logicas.Count; i++)
        {
            var linha = logicas[i];
            var texto = linha.Texto;
            var ultima = i == logicas.Count - 1;
            var fechada = !ultima || sessaoTerminou || (silencioFinal is not null && PausaFecha(texto, silencioFinal));

            var consumidos = i == 0 ? Math.Min(_consumidosNaLinhaAberta, texto.Length) : 0;
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

                _linhasFechadas += linha.LinhasFisicas;
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
            var ultimaFala = fala.LastOrDefault();
            var fimProvisorio = linhaAberta?.Fim ?? ultimaFala?.Fim ?? 0;
            eventos.Add(new EventoTempoReal(
                TipoEvento.Provisorio,
                _proximoId,
                provisorio,
                null,
                Idioma(linhaAberta?.Idioma ?? ultimaFala?.Idioma),
                linhaAberta is null ? fimProvisorio : inicioPendente,
                fimProvisorio));
        }

        return eventos;
    }

    /// <summary>
    /// Junta, a partir da primeira linha ainda não confirmada, as linhas que começam sem espaço
    /// (continuação da palavra da linha anterior).
    /// </summary>
    private static List<LinhaLogica> MontarLinhasLogicas(List<LinhaWlk> fala, int desde)
    {
        var logicas = new List<LinhaLogica>();
        for (var i = desde; i < fala.Count; i++)
        {
            var linha = fala[i];
            var texto = linha.Texto ?? string.Empty;
            if (logicas.Count > 0 && ContinuaPalavra(logicas[^1].Texto, texto))
            {
                var anterior = logicas[^1];
                logicas[^1] = anterior with
                {
                    Texto = anterior.Texto + texto,
                    Fim = linha.Fim,
                    Idioma = anterior.Idioma ?? linha.Idioma,
                    LinhasFisicas = anterior.LinhasFisicas + 1,
                };
                continue;
            }

            logicas.Add(new LinhaLogica(texto, linha.Inicio, linha.Fim, linha.Idioma, 1));
        }

        return logicas;
    }

    /// <summary>A anterior termina em letra e a próxima começa com letra, sem espaço: é a mesma palavra.</summary>
    internal static bool ContinuaPalavra(string anterior, string proxima) =>
        anterior.Length > 0 && proxima.Length > 0 && char.IsLetter(anterior[^1]) && char.IsLetter(proxima[0]);

    /// <summary>
    /// A pausa no fim fecha a linha, a não ser que a linha termine no meio de uma palavra (pode vir a
    /// continuação) e a pausa ainda seja curta.
    /// </summary>
    private static bool PausaFecha(string texto, LinhaWlk silencio)
    {
        var t = texto.TrimEnd();
        var terminaEmLetra = t.Length > 0 && t.Length == texto.Length && char.IsLetter(t[^1]);
        return !terminaEmLetra || silencio.Fim - silencio.Inicio >= PausaQueFechaPalavra.TotalSeconds;
    }

    private void Confirmar(List<EventoTempoReal> eventos, LinhaLogica linha, string textoLinha, int inicio, int fim)
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
            Idioma(linha.Idioma),
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

    private static double Estimar(LinhaLogica linha, string texto, int posicao)
    {
        if (texto.Length == 0)
        {
            return linha.Inicio;
        }

        var duracao = Math.Max(0, linha.Fim - linha.Inicio);
        return Math.Round(linha.Inicio + (duracao * posicao / texto.Length), 2);
    }

    private string? Idioma(string? detectado) =>
        _idiomaForcado ?? (string.IsNullOrWhiteSpace(detectado) ? null : Idiomas.Normalizar(detectado));
}
