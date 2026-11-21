using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Traducao.Cache;
using Transcritor.Traducao.Dicionario;
using Transcritor.Traducao.Ollama;

namespace Transcritor.Traducao;

/// <summary>
/// Tradução pt ↔ en pelo sentido, via Ollama.
/// Frases de arquivo (<see cref="TraduzirFrasesAsync"/>): modelo <see cref="OpcoesTraducao.ModeloFrases"/>, com as vizinhas como contexto.
/// Frase avulsa (<see cref="TraduzirFraseAsync"/>, usada no tempo real): <see cref="OpcoesTraducao.ModeloFrasesTempoReal"/>, mais rápido.
/// Palavras: modelo <see cref="OpcoesTraducao.ModeloPalavras"/> com a frase e a tradução dela, com cache; com o dicionário
/// configurado, o modelo escolhe entre os sentidos do dicionário (<see cref="CandidatosDicionario"/>).
/// </summary>
internal sealed class TradutorOllama(
    ClienteOllama ollama, ICachePalavras cachePalavras, IOptions<OpcoesTraducao> opcoes, FonteDicionario? fonteDicionario = null)
    : ITradutor, IServicoVerificavel
{
    private readonly OpcoesTraducao _opcoes = opcoes.Value;

    public string NomeServico => ClienteOllama.Nome;

    public string Identificador =>
        $"ollama:{_opcoes.ModeloFrases}:p{Prompts.Versao}:c{_opcoes.FrasesAntes}-{_opcoes.FrasesDepois}" +
        (Glossario.Assinatura(_opcoes.Glossario) is { Length: > 0 } g ? $":g{g}" : string.Empty);

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

        var termos = Glossario.Achar(_opcoes.Glossario, pedido.Texto, pedido.Origem);
        var dica = Glossario.Dica(termos);
        var traducao = await TraduzirUmaVezAsync(pedido, modelo, dica, cancellationToken).ConfigureAwait(false);
        if (termos.Count > 0 && !Glossario.Respeitado(traducao, termos))
        {
            // O modelo ignorou a dica: tenta com o termo já trocado no texto, sem as vizinhas (que ainda têm o original).
            var trocado = pedido with { Texto = Glossario.Substituir(pedido.Texto, termos), Anteriores = [], Seguintes = [] };
            var segunda = await TraduzirUmaVezAsync(trocado, modelo, dica, cancellationToken).ConfigureAwait(false);
            if (Glossario.Respeitado(segunda, termos))
            {
                traducao = segunda;
            }
        }

        return traducao;
    }

    private async Task<string> TraduzirUmaVezAsync(PedidoTraducaoFrase pedido, string modelo, string dica, CancellationToken cancellationToken)
    {
        if (Prompts.EhTranslateGemma(modelo))
        {
            var mensagem = Prompts.TranslateGemma(pedido, comContexto: true, dica);
            var resposta = await ollama.ConversarAsync(modelo, [mensagem], null, Prompts.MaxTokens(mensagem.Conteudo), cancellationToken)
                .ConfigureAwait(false);
            var traducao = Prompts.TemContexto(pedido) ? Prompts.RecortarAlvo(resposta, pedido.Texto) : resposta;
            if (traducao is null)
            {
                // Tags perdidas ou recorte com frases vizinhas: traduz só a frase, sem contexto.
                var semContexto = Prompts.TranslateGemma(pedido, comContexto: false, dica);
                traducao = await ollama.ConversarAsync(modelo, [semContexto], null, Prompts.MaxTokens(semContexto.Conteudo), cancellationToken)
                    .ConfigureAwait(false);
            }

            return Prompts.Limpar(traducao, pedido.Texto);
        }

        var mensagens = Prompts.FraseInstrucao(pedido, dica);
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

        if (Glossario.TraducaoExata(_opcoes.Glossario, pedido.Palavra, pedido.Origem) is { } fixa)
        {
            return fixa;
        }

        var dicionario = fonteDicionario?.Dicionario;
        var chave = ChavePalavra(pedido, dicionario is not null);
        var emCache = await cachePalavras.LerAsync(chave, cancellationToken).ConfigureAwait(false);
        if (emCache is not null)
        {
            return emCache;
        }

        // A tradução da frase é a melhor pista do sentido da palavra ("banco" → "bench" ou "bank").
        var traducaoDaFrase = string.IsNullOrWhiteSpace(pedido.TraducaoDaFrase)
            ? await TraduzirFraseAsync(new PedidoTraducaoFrase(pedido.Frase, pedido.Origem, pedido.Destino, [], []), cancellationToken).ConfigureAwait(false)
            : pedido.TraducaoDaFrase;

        // Medido (docs/modelos.md): com a tradução da frase vinda de quem chama (ex.: o JSON, feito pelo modelo maior),
        // as opções da palavra sozinha mais atrapalham que ajudam; só as de expressão ("gave up", "run out of") ajudam.
        // Com a tradução feita aqui, mais fraca, todas as opções ajudam.
        var opcoes = dicionario is null
            ? []
            : CandidatosDicionario.Buscar(
                dicionario, pedido.Palavra, pedido.Frase, pedido.Origem, pedido.Destino, traducaoDaFrase,
                soExpressao: !string.IsNullOrWhiteSpace(pedido.TraducaoDaFrase));
        var traducao = opcoes.Count == 0
            ? await TraduzirPalavraSoComModeloAsync(pedido, traducaoDaFrase, cancellationToken).ConfigureAwait(false)
            : await EscolherOpcaoAsync(pedido, traducaoDaFrase, opcoes, cancellationToken).ConfigureAwait(false);

        await cachePalavras.GravarAsync(chave, traducao, cancellationToken).ConfigureAwait(false);
        return traducao;
    }

    private async Task<string> TraduzirPalavraSoComModeloAsync(
        PedidoTraducaoPalavra pedido, string traducaoDaFrase, CancellationToken cancellationToken)
    {
        var json = await ollama.ConversarAsync(
            _opcoes.ModeloPalavras, Prompts.Palavra(pedido, traducaoDaFrase), Prompts.EsquemaTraducao, maxTokens: 64, cancellationToken).ConfigureAwait(false);
        return Prompts.Limpar(Prompts.LerJsonTraducao(json), pedido.Palavra);
    }

    /// <summary>
    /// O modelo escolhe um sentido do dicionário pelo número e adapta a tradução à frase ("desistir" → "desistiu").
    /// O texto adaptado só vale se parecer tradução: não pode ser cópia de um pedaço da frase original ("desisti",
    /// "Que pena que") nem muito mais longo que a opção (a frase inteira); senão fica a tradução da opção escolhida.
    /// Sem opção válida (0 ou fora da lista), o texto vale como tradução livre, com as mesmas regras; se não servir,
    /// traduz só com o modelo.
    /// </summary>
    private async Task<string> EscolherOpcaoAsync(
        PedidoTraducaoPalavra pedido, string traducaoDaFrase, IReadOnlyList<Candidato> opcoes, CancellationToken cancellationToken)
    {
        var json = await ollama.ConversarAsync(
            _opcoes.ModeloPalavras, Prompts.PalavraComOpcoes(pedido, traducaoDaFrase, opcoes), Prompts.EsquemaOpcao, maxTokens: 96,
            cancellationToken).ConfigureAwait(false);
        var (opcao, texto) = Prompts.LerJsonOpcao(json);
        var traducao = Prompts.Limpar(texto, pedido.Palavra);
        var escolhida = opcao is { } n && n >= 1 && n <= opcoes.Count ? opcoes[n - 1].Traducao : null;
        if (Aproveitavel(traducao, pedido.Frase, escolhida))
        {
            return traducao;
        }

        return escolhida ?? await TraduzirPalavraSoComModeloAsync(pedido, traducaoDaFrase, cancellationToken).ConfigureAwait(false);
    }

    private const int PalavrasAMaisToleradas = 3;

    /// <summary>Texto do modelo que parece tradução da palavra (não é cópia da frase original nem uma frase inteira).</summary>
    internal static bool Aproveitavel(string traducao, string fraseOriginal, string? escolhida)
    {
        var t = NormalizarTexto(traducao);
        if (t.Length == 0)
        {
            return false;
        }

        var tamanhoMaximo = (escolhida is null ? 1 : NormalizarTexto(escolhida).Split(' ').Length) + PalavrasAMaisToleradas;
        if (t.Split(' ').Length > tamanhoMaximo)
        {
            return false;
        }

        // Nomes e palavras iguais nos dois idiomas ("táxi", "James") aparecem na frase e também na opção: valem.
        var copia = $" {NormalizarTexto(fraseOriginal)} ".Contains($" {t} ", StringComparison.Ordinal);
        return !copia || (escolhida is not null && NormalizarTexto(escolhida) == t);
    }

    private static string NormalizarTexto(string texto)
    {
        var limpo = new string(texto.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(' ', limpo.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public Task VerificarAsync(CancellationToken cancellationToken) =>
        ollama.VerificarAsync([_opcoes.ModeloFrases, ModeloTempoReal, _opcoes.ModeloPalavras], cancellationToken);

    private string ChavePalavra(PedidoTraducaoPalavra pedido, bool comDicionario)
    {
        var bruto = string.Join('\u001f',
            _opcoes.ModeloPalavras,
            comDicionario ? $"dic{VersaoDicionario}" : "sem-dic",
            Glossario.Assinatura(_opcoes.Glossario),
            ModeloTempoReal,
            Prompts.Versao.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Idiomas.Normalizar(pedido.Origem),
            Idiomas.Normalizar(pedido.Destino),
            pedido.Palavra.Trim().ToLowerInvariant(),
            pedido.Frase.Trim(),
            pedido.TraducaoDaFrase?.Trim() ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bruto)));
    }

    /// <summary>Muda a escolha de opções do dicionário; entra na chave do cache de palavras.</summary>
    private const int VersaoDicionario = 3;

    private static bool MesmoIdioma(string a, string b) => Idiomas.Normalizar(a) == Idiomas.Normalizar(b);
}
