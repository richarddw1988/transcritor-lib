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
/// Com <see cref="OpcoesArquivo.DescarregarModelosEntreEtapas"/>, o modelo de cada etapa sai da GPU antes de a próxima
/// carregar o seu (<see cref="IDescarregavel"/>). Em lote (<see cref="ProcessarLoteAsync"/>), cada etapa roda para todos
/// os arquivos antes da seguinte, e cada modelo é carregado uma vez por lote. Com um <see cref="CoordenadorGpu"/>, a
/// execução espera a vez na GPU antes da primeira etapa que carrega modelo e para o tempo real até o fim.
/// </summary>
public sealed class TranscritorArquivo(
    IConversorMidia conversor,
    IMotorTranscricao motor,
    ICacheResultado cache,
    IOptions<OpcoesArquivo> opcoes,
    ITradutor? tradutor = null,
    IAnalisadorSentido? analisador = null,
    ICorretorTranscricao? corretor = null,
    CoordenadorGpu? coordenador = null) : ITranscritorArquivo
{
    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public async Task<ResultadoTranscricao> ProcessarAsync(
        string caminhoArquivo,
        Saida saida,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default) =>
        (await ProcessarLoteAsync([caminhoArquivo], saida, progresso, cancellationToken).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<ResultadoTranscricao>> ProcessarLoteAsync(
        IReadOnlyList<string> caminhosArquivos,
        Saida saida,
        IProgress<ProgressoProcessamento>? progresso = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caminhosArquivos);
        var itens = new List<ItemLote>(caminhosArquivos.Count);
        foreach (var caminhoArquivo in caminhosArquivos)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caminhoArquivo);
            var caminho = Path.GetFullPath(caminhoArquivo);
            if (!File.Exists(caminho))
            {
                throw new FileNotFoundException("Arquivo de mídia não encontrado.", caminho);
            }

            var numero = itens.Count + 1;
            itens.Add(new ItemLote(caminho, progresso is null
                ? null
                : new ProgressoDoArquivo(p => progresso.Report(p with { Arquivo = numero, TotalArquivos = caminhosArquivos.Count }))));
        }

        // Hash e transcrição do cache.
        foreach (var item in itens)
        {
            item.Progresso?.Report(new(EtapaProcessamento.CalculandoHash, 0));
            item.Hash = await HashArquivo.CalcularAsync(
                item.Caminho,
                p => item.Progresso?.Report(new(EtapaProcessamento.CalculandoHash, p)),
                cancellationToken).ConfigureAwait(false);
            item.Vocabulario = Vocabulario.Montar(
                _opcoes.Vocabulario,
                _opcoes.LerVocabularioAoLado ? await Vocabulario.LerAoLadoAsync(item.Caminho, cancellationToken).ConfigureAwait(false) : null);
            item.Chave = ChaveCache(item.Hash, item.Vocabulario);
            item.Progresso?.Report(new(EtapaProcessamento.LendoCache));
            item.Transcricao = await cache.LerAsync(item.Chave, cancellationToken).ConfigureAwait(false);
            item.TudoDoCache = item.Transcricao is not null;
        }

        // A vez na GPU (e o tempo real parado) só quando alguma etapa vai carregar modelo; tudo do cache não espera nada.
        IAsyncDisposable? reserva = null;
        async Task VezAsync()
        {
            if (reserva is not null || coordenador is null)
            {
                return;
            }

            reserva = await coordenador.ReservarAsync(
                dono =>
                {
                    foreach (var i in itens)
                    {
                        i.Progresso?.Report(new(EtapaProcessamento.AguardandoGpu, Detalhe: dono));
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Transcrição: o Whisper uma vez para todos. Modelos do Ollama que sobraram de outra execução (KeepAlive)
            // disputariam a GPU com ele.
            var transcrever = itens.Where(i => i.Transcricao is null).ToList();
            if (transcrever.Count > 0)
            {
                await VezAsync().ConfigureAwait(false);
                await DescarregarAsync([corretor, tradutor, analisador], cancellationToken).ConfigureAwait(false);
                foreach (var item in transcrever)
                {
                    item.Transcricao = await TranscreverAsync(item.Caminho, item.Hash, item.Vocabulario, item.Progresso, cancellationToken).ConfigureAwait(false);
                    await cache.GravarAsync(item.Chave, item.Transcricao, cancellationToken).ConfigureAwait(false);
                }

                await DescarregarAsync(motor, cancellationToken).ConfigureAwait(false);
            }

            // Correção de palavras ouvidas errado (opcional): antes da tradução, que passa a usar o texto corrigido.
            if (corretor is { Ativo: true })
            {
                await VezAsync().ConfigureAwait(false);
                await CorrigirLoteAsync(corretor, itens, cancellationToken).ConfigureAwait(false);
            }

            // Tradução: o modelo das frases uma vez para todos.
            var traduzir = new List<ItemLote>();
            foreach (var item in itens)
            {
                item.Resultado = item.Transcricao;
                item.Destino = RegraSaida.IdiomaDaTraducao(saida, item.Transcricao!.Arquivo.Idioma);
                if (item.Destino is null)
                {
                    continue;
                }

                var tradutorUsado = tradutor ?? throw new InvalidOperationException(
                    $"Saida.{saida} pede tradução para '{item.Destino}', mas nenhum ITradutor foi registrado. Chame services.AddTranscritorTraducao().");
                item.ChaveTraducao = $"{item.Chave}.trad-{item.Destino}.{tradutorUsado.Identificador}".Replace('/', '_');
                var traduzido = await cache.LerAsync(item.ChaveTraducao, cancellationToken).ConfigureAwait(false);
                if (traduzido is null)
                {
                    traduzir.Add(item);
                }
                else
                {
                    item.Resultado = traduzido;
                }
            }

            if (traduzir.Count > 0)
            {
                await VezAsync().ConfigureAwait(false);
                await DescarregarAsync([motor], cancellationToken).ConfigureAwait(false);
                foreach (var item in traduzir)
                {
                    item.TudoDoCache = false;
                    item.Resultado = await TraduzirAsync(tradutor!, item.Transcricao!, item.Destino!, item.Progresso, cancellationToken).ConfigureAwait(false);
                    await cache.GravarAsync(item.ChaveTraducao!, item.Resultado, cancellationToken).ConfigureAwait(false);
                }

                await DescarregarAsync(tradutor!, cancellationToken).ConfigureAwait(false);
            }

            // Análise de sentido (opcional): precisa da tradução pronta.
            if (analisador is { Ativo: true })
            {
                var analisar = new List<ItemLote>();
                foreach (var item in itens.Where(i => i.Destino is not null))
                {
                    item.ChaveAnalise = $"{item.ChaveTraducao}.sent.{analisador.Identificador}".Replace('/', '_');
                    var analisado = await cache.LerAsync(item.ChaveAnalise, cancellationToken).ConfigureAwait(false);
                    if (analisado is null)
                    {
                        analisar.Add(item);
                    }
                    else
                    {
                        item.Resultado = analisado;
                    }
                }

                if (analisar.Count > 0)
                {
                    await VezAsync().ConfigureAwait(false);
                    await DescarregarAsync([motor], cancellationToken).ConfigureAwait(false);
                    foreach (var item in analisar)
                    {
                        item.TudoDoCache = false;
                        item.Resultado = await AnalisarAsync(analisador, item.Resultado!, item.Destino!, item.Progresso, cancellationToken).ConfigureAwait(false);
                        await cache.GravarAsync(item.ChaveAnalise!, item.Resultado, cancellationToken).ConfigureAwait(false);
                    }

                    await DescarregarAsync(analisador, cancellationToken).ConfigureAwait(false);
                }
            }

        }
        finally
        {
            if (reserva is not null)
            {
                await reserva.DisposeAsync().ConfigureAwait(false);
            }
        }

        foreach (var item in itens)
        {
            item.Progresso?.Report(new(EtapaProcessamento.GravandoResultado));
            if (_opcoes.GravarJsonAoLado)
            {
                await JsonTranscricao.GravarAsync(item.Resultado!, OpcoesArquivo.CaminhoJsonAoLado(item.Caminho), cancellationToken).ConfigureAwait(false);
            }

            item.Progresso?.Report(new(EtapaProcessamento.Concluido, 100, DoCache: item.TudoDoCache));
        }

        return itens.Select(i => i.Resultado!).ToList();
    }

    /// <summary>
    /// Correção em duas fases, para o modelo de texto e o Whisper não disputarem a GPU: primeiro o modelo propõe as trocas
    /// de todas as frases de todos os arquivos; depois (com o modelo já descarregado) cada troca é conferida no áudio. O
    /// resultado é o mesmo de fazer frase a frase: a proposta de uma frase não depende das trocas aceitas nas outras.
    /// </summary>
    private async Task CorrigirLoteAsync(ICorretorTranscricao corretorAtivo, List<ItemLote> itens, CancellationToken cancellationToken)
    {
        var conferencia = _opcoes.ConferirCorrecaoNoAudio
            ? FormattableString.Invariant($".a{ConferenciaCorrecao.Versao}-{_opcoes.LimiteConfiancaReouvir}")
            : string.Empty;
        var corrigir = new List<ItemLote>();
        foreach (var item in itens)
        {
            var chaveCorrecao = $"{item.Chave}.corr.{corretorAtivo.Identificador}{conferencia}".Replace('/', '_');
            var corrigido = await cache.LerAsync(chaveCorrecao, cancellationToken).ConfigureAwait(false);
            item.Chave = chaveCorrecao;
            if (corrigido is null)
            {
                corrigir.Add(item);
            }
            else
            {
                item.Transcricao = corrigido;
            }
        }

        if (corrigir.Count == 0)
        {
            return;
        }

        // Fase 1: propostas do modelo (metade do progresso). O Whisper pode ter ficado na GPU (transcrição recém-feita
        // ou de outra execução).
        await DescarregarAsync([motor], cancellationToken).ConfigureAwait(false);
        var propostas = new List<IReadOnlyList<Correcao>[]>(corrigir.Count);
        foreach (var item in corrigir)
        {
            item.TudoDoCache = false;
            propostas.Add(await ProporAsync(corretorAtivo, item.Transcricao!, item.Progresso, cancellationToken).ConfigureAwait(false));
        }

        await DescarregarAsync([corretorAtivo, tradutor, analisador], cancellationToken).ConfigureAwait(false);

        // Fase 2: conferência no áudio e aplicação das trocas.
        var usouWhisper = false;
        for (var n = 0; n < corrigir.Count; n++)
        {
            var item = corrigir[n];
            var (corrigido, reouviu) = await ConferirAsync(item.Caminho, item.Hash, item.Transcricao!, propostas[n], item.Progresso, cancellationToken)
                .ConfigureAwait(false);
            usouWhisper |= reouviu;
            item.Transcricao = corrigido;
            await cache.GravarAsync(item.Chave, corrigido, cancellationToken).ConfigureAwait(false);
        }

        if (usouWhisper)
        {
            await DescarregarAsync(motor, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tira da GPU o modelo do serviço, se a opção estiver ligada e o serviço souber (<see cref="IDescarregavel"/>).</summary>
    private Task DescarregarAsync(object servico, CancellationToken cancellationToken) =>
        DescarregarAsync([servico], cancellationToken);

    /// <summary>
    /// Tira da GPU os modelos dos serviços, um por vez. Antes de cada etapa a lib tira os do outro serviço (Whisper antes
    /// das etapas do Ollama e vice-versa), mesmo que tenham sido carregados por outra execução; o Ollama troca sozinho
    /// os modelos dele entre si.
    /// </summary>
    private async Task DescarregarAsync(IEnumerable<object?> servicos, CancellationToken cancellationToken)
    {
        if (!_opcoes.DescarregarModelosEntreEtapas)
        {
            return;
        }

        foreach (var d in servicos.OfType<IDescarregavel>().Distinct())
        {
            await d.DescarregarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hash do arquivo + motor/modelo + idioma forçado + vocabulário: trocar o modelo ou a dica não reaproveita
    /// resultado de outro.
    /// </summary>
    internal string ChaveCache(string hash, string? vocabulario = null)
    {
        var idioma = !string.IsNullOrWhiteSpace(_opcoes.Idioma) ? Idiomas.Normalizar(_opcoes.Idioma)
            : _opcoes.ConferirIdioma ? $"auto-c{ConferenciaIdioma.Versao}" : "auto";
        var reparo = _opcoes.RepararTrechosSuspeitos ? $".r{ReparadorTrechos.Versao}" : string.Empty;
        var dica = vocabulario is null ? string.Empty : $".v{Vocabulario.Chave(vocabulario)}";
        var pedacos = _opcoes.SegundosPorPedaco > 0
            ? $".p{PedacosAudio.Versao}-{_opcoes.SegundosPorPedaco.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;
        return $"{hash}.{motor.Identificador}.{idioma}.m{MontadorFrases.Versao}{reparo}{dica}{pedacos}".Replace('/', '_');
    }

    /// <summary>
    /// Transcreve de novo, isolado, cada trecho em que o motor devolveu palavras sem som alinhado (alucinação),
    /// e troca o trecho se o resultado novo for melhor. Usa o idioma detectado na transcrição inteira.
    /// </summary>
    private async Task<TranscricaoBruta> RepararAsync(
        string wav, TranscricaoBruta bruta, string pastaTemporaria, string? vocabulario, CancellationToken cancellationToken)
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
                var contexto = Vocabulario.ComContexto(vocabulario, ReparadorTrechos.ContextoAntes(bruta.Palavras, janela));
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

    /// <summary>
    /// Transcreve o WAV inteiro. Sem idioma forçado e com <see cref="OpcoesArquivo.ConferirIdioma"/>, antes confere a
    /// detecção numa amostra do começo (<see cref="ConferenciaIdioma"/>) e transcreve com o idioma vencedor. Arquivo
    /// que cabe na amostra não é transcrito de novo: o resultado da amostra vencedora já é o do arquivo inteiro (a não
    /// ser com vocabulário). A conferência roda sem o vocabulário, que não foi medido na detecção do idioma; só a
    /// transcrição final leva a dica.
    /// </summary>
    private async Task<TranscricaoBruta> TranscreverConferindoIdiomaAsync(
        string wav, double duracao, string pastaTemporaria, string? vocabulario, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_opcoes.Idioma) || !_opcoes.ConferirIdioma)
        {
            return await TranscreverEmPedacosAsync(wav, duracao, _opcoes.Idioma, vocabulario, pastaTemporaria, cancellationToken).ConfigureAwait(false);
        }

        var cabeNaAmostra = duracao > 0 && duracao <= _opcoes.SegundosConferirIdioma;
        var amostra = cabeNaAmostra ? wav : Path.Combine(pastaTemporaria, $"{Guid.NewGuid():N}.idioma.wav");
        try
        {
            if (!cabeNaAmostra)
            {
                try
                {
                    await RecorteWav.RecortarAsync(wav, amostra, 0, _opcoes.SegundosConferirIdioma, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return await TranscreverEmPedacosAsync(wav, duracao, null, vocabulario, pastaTemporaria, cancellationToken).ConfigureAwait(false);
                }
            }

            var detectada = await motor.TranscreverAsync(amostra, null, cancellationToken).ConfigureAwait(false);
            var outro = ConferenciaIdioma.Outro(detectada.Idioma);
            var escolhida = detectada;
            if (outro is not null && ConferenciaIdioma.ConfiancaMedia(detectada) is not null)
            {
                var alternativa = await motor.TranscreverAsync(amostra, outro, cancellationToken).ConfigureAwait(false);
                if (ConferenciaIdioma.AlternativaGanha(detectada, alternativa))
                {
                    escolhida = alternativa with { Idioma = outro };
                }
            }

            if (cabeNaAmostra && vocabulario is null)
            {
                return escolhida;
            }

            // O idioma vai forçado também quando a detecção ganhou: o arquivo inteiro sai no idioma conferido.
            var idioma = outro is null ? null : Idiomas.Normalizar(escolhida.Idioma);
            var final = await TranscreverEmPedacosAsync(wav, duracao, idioma, vocabulario, pastaTemporaria, cancellationToken).ConfigureAwait(false);
            return idioma is null && cabeNaAmostra ? final with { Idioma = escolhida.Idioma } : final;
        }
        finally
        {
            if (!cabeNaAmostra && File.Exists(amostra))
            {
                File.Delete(amostra);
            }
        }
    }

    /// <summary>
    /// Transcreve o WAV inteiro em pedaços de <see cref="OpcoesArquivo.SegundosPorPedaco"/> (ver <see cref="PedacosAudio"/>),
    /// todos com o mesmo idioma e vocabulário; cada um leva como dica as últimas palavras do anterior. As frases que o
    /// Whisper inventa em trecho sem fala ("Thanks for watching!") saem. Áudio curto, opção desligada ou WAV
    /// ilegível: uma chamada só.
    /// </summary>
    private async Task<TranscricaoBruta> TranscreverEmPedacosAsync(
        string wav, double duracao, string? idioma, string? vocabulario, string pastaTemporaria, CancellationToken cancellationToken)
    {
        var tamanho = _opcoes.SegundosPorPedaco;
        if (tamanho <= 0 || duracao <= tamanho * 1.5)
        {
            return await motor.TranscreverAsync(wav, idioma, vocabulario, cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes;
        double[] energia;
        try
        {
            bytes = await File.ReadAllBytesAsync(wav, cancellationToken).ConfigureAwait(false);
            energia = PedacosAudio.Energia(bytes);
        }
        catch (InvalidDataException)
        {
            return await motor.TranscreverAsync(wav, idioma, vocabulario, cancellationToken).ConfigureAwait(false);
        }

        var limites = PedacosAudio.Limites(energia, duracao, tamanho, busca: Math.Min(30, tamanho / 4));
        var recorte = Path.Combine(pastaTemporaria, $"{Guid.NewGuid():N}.pedaco.wav");
        try
        {
            var pedacos = new List<(double, double, TranscricaoBruta)>(limites.Count);
            string? anterior = null;
            foreach (var (inicio, fim) in limites)
            {
                await RecorteWav.RecortarAsync(bytes, recorte, inicio, fim, cancellationToken).ConfigureAwait(false);
                var dica = Vocabulario.ComContexto(vocabulario, anterior);
                var bruta = PedacosAudio.SemFrasesInventadas(
                    await motor.TranscreverAsync(recorte, idioma, dica, cancellationToken).ConfigureAwait(false));
                pedacos.Add((inicio, fim, bruta));
                anterior = PedacosAudio.ContextoDoFim(bruta) ?? anterior;
            }

            return PedacosAudio.Juntar(pedacos, duracao);
        }
        finally
        {
            if (File.Exists(recorte))
            {
                File.Delete(recorte);
            }
        }
    }

    /// <summary>
    /// Faixa de áudio a converter: com <see cref="OpcoesArquivo.Idioma"/> forçado, a primeira marcada com esse idioma
    /// (filme dublado com "por" e "eng"); sem idioma, ou sem faixa marcada com ele, a primeira.
    /// </summary>
    internal static int EscolherFaixa(InfoMidia info, string? idioma)
    {
        if (string.IsNullOrWhiteSpace(idioma) || info.IdiomasFaixas is not { Count: > 1 } idiomas)
        {
            return 0;
        }

        var procurado = Idiomas.Normalizar(idioma);
        for (var i = 0; i < idiomas.Count; i++)
        {
            if (idiomas[i] == procurado)
            {
                return i;
            }
        }

        return 0;
    }

    private async Task<ResultadoTranscricao> TranscreverAsync(
        string caminho, string hash, string? vocabulario, IProgress<ProgressoProcessamento>? progresso, CancellationToken cancellationToken)
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
                EscolherFaixa(info, _opcoes.Idioma),
                segundos => progresso?.Report(new(EtapaProcessamento.ConvertendoAudio, Percentual(segundos, info.Duracao))),
                cancellationToken).ConfigureAwait(false);
            progresso?.Report(new(EtapaProcessamento.ConvertendoAudio, 100));

            progresso?.Report(new(EtapaProcessamento.Transcrevendo));
            var bruta = await TranscreverConferindoIdiomaAsync(wav, info.Duracao, pastaTemporaria, vocabulario, cancellationToken).ConfigureAwait(false);
            if (_opcoes.RepararTrechosSuspeitos)
            {
                bruta = await RepararAsync(wav, bruta, pastaTemporaria, vocabulario, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Fase 1 da correção: as trocas que o modelo propõe para cada frase (0 a 50% do progresso).</summary>
    private static async Task<IReadOnlyList<Correcao>[]> ProporAsync(
        ICorretorTranscricao corretorAtivo,
        ResultadoTranscricao transcricao,
        IProgress<ProgressoProcessamento>? progresso,
        CancellationToken cancellationToken)
    {
        progresso?.Report(new(EtapaProcessamento.CorrigindoTranscricao, 0));
        var frases = transcricao.Frases;
        var propostas = new IReadOnlyList<Correcao>[frases.Count];
        for (var i = 0; i < frases.Count; i++)
        {
            var f = frases[i];
            var pedido = new PedidoCorrecao(
                f.Palavras.Select(p => p.Texto).ToList(),
                transcricao.Arquivo.Idioma,
                frases.Skip(Math.Max(0, i - 2)).Take(Math.Min(i, 2)).Select(x => x.Texto).ToList(),
                frases.Skip(i + 1).Take(1).Select(x => x.Texto).ToList(),
                f.Palavras.Select(p => p.Confianca).ToList());
            propostas[i] = f.Palavras.Count == 0
                ? []
                : (await corretorAtivo.CorrigirAsync(pedido, cancellationToken).ConfigureAwait(false))
                    .Where(c => c.Indice >= 0 && c.Indice < f.Palavras.Count)
                    .ToList();
            progresso?.Report(new(EtapaProcessamento.CorrigindoTranscricao, (i + 1) * 50d / frases.Count));
        }

        return propostas;
    }

    /// <summary>
    /// Fase 2 da correção: confere cada troca proposta no áudio (se <see cref="OpcoesArquivo.ConferirCorrecaoNoAudio"/>) e
    /// aplica as aceitas (50 a 100% do progresso). Diz também se chamou o Whisper.
    /// </summary>
    private async Task<(ResultadoTranscricao Corrigido, bool Reouviu)> ConferirAsync(
        string caminho,
        string hash,
        ResultadoTranscricao transcricao,
        IReadOnlyList<Correcao>[] propostas,
        IProgress<ProgressoProcessamento>? progresso,
        CancellationToken cancellationToken)
    {
        var frases = transcricao.Frases;
        var pastaTemporaria = Path.Combine(Path.GetTempPath(), "transcritor");
        var wav = Path.Combine(pastaTemporaria, $"{hash}-{Guid.NewGuid():N}.corr.wav");
        var recorte = Path.Combine(pastaTemporaria, $"{Guid.NewGuid():N}.corr.trecho.wav");
        var wavPronto = false;
        try
        {
            var corrigidas = new List<Frase>(frases.Count);
            for (var i = 0; i < frases.Count; i++)
            {
                var f = frases[i];
                var palavras = f.Palavras.ToList();
                var atual = f.Palavras.Select(p => p.Texto).ToArray();
                var aceitas = 0;
                foreach (var c in propostas[i])
                {
                    // Homófono soa igual: reouvir não confirma nem recusa a troca.
                    if (_opcoes.ConferirCorrecaoNoAudio && !c.Homofono)
                    {
                        // O áudio só é convertido de novo se alguma frase tiver troca para conferir.
                        if (!wavPronto)
                        {
                            Directory.CreateDirectory(pastaTemporaria);
                            var info = await conversor.ObterInfoAsync(caminho, cancellationToken).ConfigureAwait(false);
                            await conversor.ConverterParaWavAsync(
                                caminho, wav, EscolherFaixa(info, _opcoes.Idioma), null, cancellationToken).ConfigureAwait(false);
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
                progresso?.Report(new(EtapaProcessamento.CorrigindoTranscricao, 50 + ((i + 1) * 50d / frases.Count)));
            }

            return (transcricao with { Frases = corrigidas }, wavPronto);
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

    /// <summary>Estado de um arquivo do lote entre as etapas.</summary>
    private sealed class ItemLote(string caminho, IProgress<ProgressoProcessamento>? progresso)
    {
        public string Caminho { get; } = caminho;

        public IProgress<ProgressoProcessamento>? Progresso { get; } = progresso;

        public string Hash { get; set; } = string.Empty;

        /// <summary>Dica mandada ao Whisper (opção + arquivo ao lado da mídia), já montada; nula sem vocabulário.</summary>
        public string? Vocabulario { get; set; }

        /// <summary>Chave do cache da transcrição (ou da transcrição corrigida, depois da correção).</summary>
        public string Chave { get; set; } = string.Empty;

        public ResultadoTranscricao? Transcricao { get; set; }

        public ResultadoTranscricao? Resultado { get; set; }

        public string? Destino { get; set; }

        public string? ChaveTraducao { get; set; }

        public string? ChaveAnalise { get; set; }

        public bool TudoDoCache { get; set; }
    }

    /// <summary>Repassa na mesma thread, acrescentando o número do arquivo no lote.</summary>
    private sealed class ProgressoDoArquivo(Action<ProgressoProcessamento> acao) : IProgress<ProgressoProcessamento>
    {
        public void Report(ProgressoProcessamento value) => acao(value);
    }
}
