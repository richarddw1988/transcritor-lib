using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao;

/// <summary>
/// Tradução pt ↔ en pelo sentido, via Ollama.
/// Frases de arquivo (<see cref="TraduzirFrasesAsync"/>): modelo <see cref="OpcoesTraducao.ModeloFrases"/>, com as vizinhas como contexto.
/// Frase avulsa (<see cref="TraduzirFraseAsync"/>, usada no tempo real): <see cref="OpcoesTraducao.ModeloFrasesTempoReal"/>, mais rápido.
/// Palavras: modelo <see cref="OpcoesTraducao.ModeloPalavras"/> com a frase e a tradução dela, com cache.
/// </summary>
internal sealed class TradutorOllama(ClienteOllama ollama, ICachePalavras cachePalavras, IOptions<OpcoesTraducao> opcoes)
    : ITradutor, IServicoVerificavel
{
    private readonly OpcoesTraducao _opcoes = opcoes.Value;

    public string NomeServico => ClienteOllama.Nome;

    public string Identificador =>
        $"ollama:{_opcoes.ModeloFrases}:p{Prompts.Versao}:c{_opcoes.FrasesAntes}-{_opcoes.FrasesDepois}";

    public Task<string> TraduzirFraseAsync(PedidoTraducaoFrase pedido, CancellationToken cancellationToken) =>
        TraduzirComModeloAsync(pedido, ModeloTempoReal, cancellationToken);

    /// <summary>Modelo da frase avulsa; vazio na configuração = mesmo modelo do arquivo.</summary>
    private string ModeloTempoReal =>
        string.IsNullOrWhiteSpace(_opcoes.ModeloFrasesTempoReal) ? _opcoes.ModeloFrases : _opcoes.ModeloFrasesTempoReal;

    private async Task<string> TraduzirComModeloAsync(PedidoTraducaoFrase pedido, string modelo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (string.IsNullOrWhiteSpace(pedido.Texto) || MesmoIdioma(pedido.Origem, pedido.Destino))
        {
            return pedido.Texto;
        }

        if (Prompts.EhTranslateGemma(modelo))
        {
            var mensagem = Prompts.TranslateGemma(pedido, comContexto: true);
            var resposta = await ollama.ConversarAsync(modelo, [mensagem], null, Prompts.MaxTokens(mensagem.Conteudo), cancellationToken)
                .ConfigureAwait(false);
            var traducao = Prompts.TemContexto(pedido) ? Prompts.RecortarAlvo(resposta, pedido.Texto) : resposta;
            if (traducao is null)
            {
                // Tags perdidas ou recorte com frases vizinhas: traduz só a frase, sem contexto.
                var semContexto = Prompts.TranslateGemma(pedido, comContexto: false);
                traducao = await ollama.ConversarAsync(modelo, [semContexto], null, Prompts.MaxTokens(semContexto.Conteudo), cancellationToken)
                    .ConfigureAwait(false);
            }

            return Prompts.Limpar(traducao, pedido.Texto);
        }

        var mensagens = Prompts.FraseInstrucao(pedido);
        var json = await ollama.ConversarAsync(
            modelo, mensagens, Prompts.EsquemaTraducao, Prompts.MaxTokens(mensagens[^1].Conteudo), cancellationToken).ConfigureAwait(false);
        return Prompts.Limpar(Prompts.LerJsonTraducao(json), pedido.Texto);
    }

    public async Task<IReadOnlyList<string>> TraduzirFrasesAsync(
        IReadOnlyList<string> frases,
        string origem,
        string destino,
        IProgress<double>? progresso,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frases);
        var antes = Math.Max(0, _opcoes.FrasesAntes);
        var depois = Math.Max(0, _opcoes.FrasesDepois);

        var traducoes = new string[frases.Count];
        for (var i = 0; i < frases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inicio = Math.Max(0, i - antes);
            var pedido = new PedidoTraducaoFrase(
                frases[i],
                origem,
                destino,
                frases.Skip(inicio).Take(i - inicio).ToList(),
                frases.Skip(i + 1).Take(depois).ToList());
            traducoes[i] = await TraduzirComModeloAsync(pedido, _opcoes.ModeloFrases, cancellationToken).ConfigureAwait(false);
            progresso?.Report((i + 1) * 100d / frases.Count);
        }

        return traducoes;
    }

    public async Task<string> TraduzirPalavraAsync(PedidoTraducaoPalavra pedido, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        if (string.IsNullOrWhiteSpace(pedido.Palavra) || MesmoIdioma(pedido.Origem, pedido.Destino))
        {
            return pedido.Palavra;
        }

        var chave = ChavePalavra(pedido);
        var emCache = await cachePalavras.LerAsync(chave, cancellationToken).ConfigureAwait(false);
        if (emCache is not null)
        {
            return emCache;
        }

        // A tradução da frase é a melhor pista do sentido da palavra ("banco" → "bench" ou "bank").
        var traducaoDaFrase = string.IsNullOrWhiteSpace(pedido.TraducaoDaFrase)
            ? await TraduzirFraseAsync(new PedidoTraducaoFrase(pedido.Frase, pedido.Origem, pedido.Destino, [], []), cancellationToken).ConfigureAwait(false)
            : pedido.TraducaoDaFrase;

        var json = await ollama.ConversarAsync(
            _opcoes.ModeloPalavras, Prompts.Palavra(pedido, traducaoDaFrase), Prompts.EsquemaTraducao, maxTokens: 64, cancellationToken).ConfigureAwait(false);
        var traducao = Prompts.Limpar(Prompts.LerJsonTraducao(json), pedido.Palavra);

        await cachePalavras.GravarAsync(chave, traducao, cancellationToken).ConfigureAwait(false);
        return traducao;
    }

    public Task VerificarAsync(CancellationToken cancellationToken) =>
        ollama.VerificarAsync([_opcoes.ModeloFrases, ModeloTempoReal, _opcoes.ModeloPalavras], cancellationToken);

    private string ChavePalavra(PedidoTraducaoPalavra pedido)
    {
        var bruto = string.Join('\u001f',
            _opcoes.ModeloPalavras,
            ModeloTempoReal,
            Prompts.Versao.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Idiomas.Normalizar(pedido.Origem),
            Idiomas.Normalizar(pedido.Destino),
            pedido.Palavra.Trim().ToLowerInvariant(),
            pedido.Frase.Trim(),
            pedido.TraducaoDaFrase?.Trim() ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bruto)));
    }

    private static bool MesmoIdioma(string a, string b) => Idiomas.Normalizar(a) == Idiomas.Normalizar(b);
}
