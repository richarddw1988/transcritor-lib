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
    IAnalisadorSentido? analisador = null,
    ICorretorTranscricao? corretor = null) : ITranscritorArquivo
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

        // Correção de palavras ouvidas errado (opcional): antes da tradução, que passa a usar o texto corrigido.
        if (corretor is { Ativo: true })
        {
            var conferencia = _opcoes.ConferirCorrecaoNoAudio
                ? FormattableString.Invariant($".a{ConferenciaCorrecao.Versao}-{_opcoes.LimiteConfiancaReouvir}")
                : string.Empty;
            var chaveCorrecao = $"{chave}.corr.{corretor.Identificador}{conferencia}".Replace('/', '_');
            var corrigido = await cache.LerAsync(chaveCorrecao, cancellationToken).ConfigureAwait(false);
            if (corrigido is null)
            {
                tudoDoCache = false;
                corrigido = await CorrigirAsync(corretor, caminho, hash, transcricao, progresso, cancellationToken).ConfigureAwait(false);
                await cache.GravarAsync(chaveCorrecao, corrigido, cancellationToken).ConfigureAwait(false);
            }

            transcricao = corrigido;
            chave = chaveCorrecao;
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
        var reparo = _opcoes.RepararTrechosSuspeitos ? $".r{ReparadorTrechos.Versao}" : string.Empty;
        return $"{hash}.{motor.Identificador}.{idioma}.m{MontadorFrases.Versao}{reparo}".Replace('/', '_');
    }

    /// <summary>
    /// Transcreve de novo, isolado, cada trecho em que o motor devolveu palavras sem som alinhado (alucinação),
    /// e troca o trecho se o resultado novo for melhor. Usa o idioma detectado na transcrição inteira.
    /// </summary>
    private async Task<TranscricaoBruta> RepararAsync(
        string wav, TranscricaoBruta bruta, string pastaTemporaria, CancellationToken cancellationToken)
    {
        var janelas = ReparadorTrechos.Detectar(bruta.Palavras, bruta.Duracao);
        if (janelas.Count == 0)
        {
            return bruta;
        }

        var recorte = Path.Combine(pastaTemporaria, $"{Guid.NewGuid():N}.trecho.wav");
        try
        {
            foreach (var janela in janelas.Take(_opcoes.MaximoTrechosReparados))
            {
                try
                {
                    await RecorteWav.RecortarAsync(wav, recorte, janela.Inicio, janela.Fim, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return bruta;
                }

                var idioma = string.IsNullOrWhiteSpace(_opcoes.Idioma) ? bruta.Idioma : _opcoes.Idioma;
                var contexto = ReparadorTrechos.ContextoAntes(bruta.Palavras, janela);
                var nova = await motor.TranscreverAsync(recorte, idioma, contexto, cancellationToken).ConfigureAwait(false);
                if (ReparadorTrechos.Melhorou(bruta, janela, nova))
                {
                    bruta = ReparadorTrechos.Substituir(bruta, janela, nova);
                }
            }

            return bruta;
        }
        finally
        {
            if (File.Exists(recorte))
            {
                File.Delete(recorte);
            }
        }
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
            if (_opcoes.RepararTrechosSuspeitos)
            {
                bruta = await RepararAsync(wav, bruta, pastaTemporaria, cancellationToken).ConfigureAwait(false);
            }

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

    private async Task<ResultadoTranscricao> CorrigirAsync(
        ICorretorTranscricao corretor,
        string caminho,
        string hash,
        ResultadoTranscricao transcricao,
        IProgress<ProgressoProcessamento>? progresso,
        CancellationToken cancellationToken)
    {
        progresso?.Report(new(EtapaProcessamento.CorrigindoTranscricao, 0));
        var pastaTemporaria = Path.Combine(Path.GetTempPath(), "transcritor");
        var wav = Path.Combine(pastaTemporaria, $"{hash}-{Guid.NewGuid():N}.corr.wav");
        var recorte = Path.Combine(pastaTemporaria, $"{Guid.NewGuid():N}.corr.trecho.wav");
        var wavPronto = false;
        try
        {
            var frases = transcricao.Frases;
            var corrigidas = new List<Frase>(frases.Count);
            for (var i = 0; i < frases.Count; i++)
            {
                var f = frases[i];
                var pedido = new PedidoCorrecao(
                    f.Palavras.Select(p => p.Texto).ToList(),
                    transcricao.Arquivo.Idioma,
                    frases.Skip(Math.Max(0, i - 2)).Take(Math.Min(i, 2)).Select(x => x.Texto).ToList(),
                    frases.Skip(i + 1).Take(1).Select(x => x.Texto).ToList(),
                    f.Palavras.Select(p => p.Confianca).ToList());
                var correcoes = f.Palavras.Count == 0 ? [] : await corretor.CorrigirAsync(pedido, cancellationToken).ConfigureAwait(false);
                var palavras = f.Palavras.ToList();
                var atual = f.Palavras.Select(p => p.Texto).ToArray();
                var aceitas = 0;
                foreach (var c in correcoes.Where(c => c.Indice >= 0 && c.Indice < palavras.Count))
                {
                    if (_opcoes.ConferirCorrecaoNoAudio)
                    {
                        // O áudio só é convertido de novo se alguma frase tiver troca para conferir.
                        if (!wavPronto)
                        {
                            Directory.CreateDirectory(pastaTemporaria);
                            await conversor.ConverterParaWavAsync(caminho, wav, null, cancellationToken).ConfigureAwait(false);
                            wavPronto = true;
                        }

                        var trocada = atual.ToArray();
                        trocada[c.Indice] = c.Corrigida;
                        await RecorteWav.RecortarAsync(
                            wav, recorte, f.Inicio - ConferenciaCorrecao.Margem, f.Fim + ConferenciaCorrecao.Margem, cancellationToken).ConfigureAwait(false);
                        var reouvida = await motor.TranscreverAsync(
                            recorte, transcricao.Arquivo.Idioma, string.Join(' ', trocada), cancellationToken).ConfigureAwait(false);
                        if (!ConferenciaCorrecao.Confirma(trocada, c.Indice, reouvida, _opcoes.LimiteConfiancaReouvir))
                        {
                            continue;
                        }
                    }

                    atual[c.Indice] = c.Corrigida;
                    palavras[c.Indice] = palavras[c.Indice] with { Texto = c.Corrigida, Ouvida = palavras[c.Indice].Ouvida ?? palavras[c.Indice].Texto };
                    aceitas++;
                }

                corrigidas.Add(aceitas == 0 ? f : f with { Palavras = palavras, Texto = string.Join(' ', palavras.Select(p => p.Texto)) });
                progresso?.Report(new(EtapaProcessamento.CorrigindoTranscricao, (i + 1) * 100d / frases.Count));
            }

            return transcricao with { Frases = corrigidas };
        }
        finally
        {
            foreach (var temporario in new[] { wav, recorte }.Where(File.Exists))
            {
                File.Delete(temporario);
            }
        }
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
