using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Hash → cache → ffprobe → ffmpeg (WAV 16 kHz mono) → motor de transcrição → frases → tradução → cache e JSON.
/// A transcrição e cada tradução têm cache próprio: repetir o mesmo pedido não chama nenhum serviço,
/// e trocar só a <see cref="Saida"/> reaproveita a transcrição.
/// </summary>
public sealed class TranscritorArquivo(
    IConversorMidia conversor,
    IMotorTranscricao motor,
    ICacheResultado cache,
    IOptions<OpcoesArquivo> opcoes,
    ITradutor? tradutor = null,
    IAnalisadorSentido? analisador = null) : ITranscritorArquivo
{
    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public async Task<ResultadoTranscricao> ProcessarAsync(
        string caminhoArquivo,
        Saida saida,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caminhoArquivo);
        var caminho = Path.GetFullPath(caminhoArquivo);
        if (!File.Exists(caminho))
        {
            throw new FileNotFoundException("Arquivo de mídia não encontrado.", caminho);
        }

        progresso?.Report(new(EtapaProcessamento.CalculandoHash, 0));
        var hash = await HashArquivo.CalcularAsync(
            caminho,
            p => progresso?.Report(new(EtapaProcessamento.CalculandoHash, p)),
            cancellationToken).ConfigureAwait(false);

        var chave = ChaveCache(hash);
        progresso?.Report(new(EtapaProcessamento.LendoCache));
        var transcricao = await cache.LerAsync(chave, cancellationToken).ConfigureAwait(false);
        var tudoDoCache = transcricao is not null;
        if (transcricao is null)
        {
            transcricao = await TranscreverAsync(caminho, hash, progresso, cancellationToken).ConfigureAwait(false);
            await cache.GravarAsync(chave, transcricao, cancellationToken).ConfigureAwait(false);
        }

        var resultado = transcricao;
        var destino = RegraSaida.IdiomaDaTraducao(saida, transcricao.Arquivo.Idioma);
        if (destino is not null)
        {
            var tradutorUsado = tradutor ?? throw new InvalidOperationException(
                $"Saida.{saida} pede tradução para '{destino}', mas nenhum ITradutor foi registrado. Chame services.AddTranscritorTraducao().");

            var chaveTraducao = $"{chave}.trad-{destino}.{tradutorUsado.Identificador}".Replace('/', '_');
            var traduzido = await cache.LerAsync(chaveTraducao, cancellationToken).ConfigureAwait(false);
            if (traduzido is null)
            {
                tudoDoCache = false;
                traduzido = await TraduzirAsync(tradutorUsado, transcricao, destino, progresso, cancellationToken).ConfigureAwait(false);
                await cache.GravarAsync(chaveTraducao, traduzido, cancellationToken).ConfigureAwait(false);
            }

            resultado = traduzido;

            // Análise de sentido (opcional): precisa da tradução pronta.
            if (analisador is { Ativo: true })
            {
                var chaveAnalise = $"{chaveTraducao}.sent.{analisador.Identificador}".Replace('/', '_');
                var analisado = await cache.LerAsync(chaveAnalise, cancellationToken).ConfigureAwait(false);
                if (analisado is null)
                {
                    tudoDoCache = false;
                    analisado = await AnalisarAsync(analisador, traduzido, destino, progresso, cancellationToken).ConfigureAwait(false);
                    await cache.GravarAsync(chaveAnalise, analisado, cancellationToken).ConfigureAwait(false);
                }

                resultado = analisado;
            }
        }

        progresso?.Report(new(EtapaProcessamento.GravandoResultado));
        if (_opcoes.GravarJsonAoLado)
        {
            await JsonTranscricao.GravarAsync(resultado, OpcoesArquivo.CaminhoJsonAoLado(caminho), cancellationToken).ConfigureAwait(false);
        }

        progresso?.Report(new(EtapaProcessamento.Concluido, 100, DoCache: tudoDoCache));
        return resultado;
    }

    /// <summary>Hash do arquivo + motor/modelo + idioma forçado: trocar o modelo não reaproveita resultado de outro.</summary>
    internal string ChaveCache(string hash)
    {
        var idioma = string.IsNullOrWhiteSpace(_opcoes.Idioma) ? "auto" : Idiomas.Normalizar(_opcoes.Idioma);
        return $"{hash}.{motor.Identificador}.{idioma}.m{MontadorFrases.Versao}".Replace('/', '_');
    }

    private async Task<ResultadoTranscricao> TranscreverAsync(
        string caminho, string hash, IProgress<ProgressoProcessamento>? progresso, CancellationToken cancellationToken)
    {
        progresso?.Report(new(EtapaProcessamento.LendoMidia));
        var info = await conversor.ObterInfoAsync(caminho, cancellationToken).ConfigureAwait(false);
        if (info.FaixasAudio == 0)
        {
            throw new TranscritorException($"O arquivo '{caminho}' não tem faixa de áudio.");
        }

        var pastaTemporaria = Path.Combine(Path.GetTempPath(), "transcritor");
        Directory.CreateDirectory(pastaTemporaria);
        var wav = Path.Combine(pastaTemporaria, $"{hash}-{Guid.NewGuid():N}.wav");
        try
        {
            progresso?.Report(new(EtapaProcessamento.ConvertendoAudio, 0));
            await conversor.ConverterParaWavAsync(
                caminho,
                wav,
                segundos => progresso?.Report(new(EtapaProcessamento.ConvertendoAudio, Percentual(segundos, info.Duracao))),
                cancellationToken).ConfigureAwait(false);
            progresso?.Report(new(EtapaProcessamento.ConvertendoAudio, 100));

            progresso?.Report(new(EtapaProcessamento.Transcrevendo));
            var bruta = await motor.TranscreverAsync(wav, _opcoes.Idioma, cancellationToken).ConfigureAwait(false);

            progresso?.Report(new(EtapaProcessamento.MontandoFrases));
            var frases = MontadorFrases.Montar(bruta);
            var duracao = Math.Round(info.Duracao > 0 ? info.Duracao : bruta.Duracao, 3);
            return new ResultadoTranscricao(new InfoArquivo(hash, duracao, Idiomas.Normalizar(bruta.Idioma)), frases);
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
    }

    private static async Task<ResultadoTranscricao> TraduzirAsync(
        ITradutor tradutor,
        ResultadoTranscricao transcricao,
        string destino,
        IProgress<ProgressoProcessamento>? progresso,
        CancellationToken cancellationToken)
    {
        progresso?.Report(new(EtapaProcessamento.Traduzindo, 0));
        var textos = transcricao.Frases.Select(f => f.Texto).ToList();
        var traducoes = await tradutor.TraduzirFrasesAsync(
            textos,
            transcricao.Arquivo.Idioma,
            destino,
            new ProgressoRepassado(p => progresso?.Report(new(EtapaProcessamento.Traduzindo, p))),
            cancellationToken).ConfigureAwait(false);

        if (traducoes.Count != textos.Count)
        {
            throw new TranscritorException($"O tradutor devolveu {traducoes.Count} traduções para {textos.Count} frases.");
        }

        var frases = transcricao.Frases.Select((f, i) => f with { Traducao = traducoes[i] }).ToList();
        return transcricao with { Frases = frases };
    }

    private static async Task<ResultadoTranscricao> AnalisarAsync(
        IAnalisadorSentido analisador,
        ResultadoTranscricao traduzido,
        string destino,
        IProgress<ProgressoProcessamento>? progresso,
        CancellationToken cancellationToken)
    {
        progresso?.Report(new(EtapaProcessamento.AnalisandoSentido, 0));
        var frases = traduzido.Frases;
        var analisadas = new List<Frase>(frases.Count);
        for (var i = 0; i < frases.Count; i++)
        {
            var f = frases[i];
            if (string.IsNullOrWhiteSpace(f.Traducao) || f.Palavras.Count == 0)
            {
                analisadas.Add(f);
                continue;
            }

            var pedido = new PedidoAnaliseFrase(
                f.Palavras.Select(p => p.Texto).ToList(),
                f.Traducao,
                traduzido.Arquivo.Idioma,
                destino,
                frases.Skip(Math.Max(0, i - 2)).Take(Math.Min(i, 2)).Select(x => x.Texto).ToList(),
                frases.Skip(i + 1).Take(1).Select(x => x.Texto).ToList());
            var analise = await analisador.AnalisarAsync(pedido, cancellationToken).ConfigureAwait(false);
            analisadas.Add(f with { Sentido = analise.Sentido, Blocos = analise.Blocos });
            progresso?.Report(new(EtapaProcessamento.AnalisandoSentido, (i + 1) * 100d / frases.Count));
        }

        return traduzido with { Frases = analisadas };
    }

    private static double? Percentual(double segundos, double duracao) =>
        duracao > 0 ? Math.Clamp(segundos * 100 / duracao, 0, 100) : null;

    /// <summary>Repassa na mesma thread (sem SynchronizationContext, ao contrário de Progress&lt;T&gt;).</summary>
    private sealed class ProgressoRepassado(Action<double> acao) : IProgress<double>
    {
        public void Report(double value) => acao(value);
    }
}
