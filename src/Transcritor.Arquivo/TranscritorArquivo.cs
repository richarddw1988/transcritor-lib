using Microsoft.Extensions.Options;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Contratos;
using Transcritor.Core.Erros;
using Transcritor.Core.Modelos;

namespace Transcritor.Arquivo;

/// <summary>
/// Hash → cache → ffprobe → ffmpeg (WAV 16 kHz mono) → motor de transcrição → frases → cache e JSON.
/// Com cache válido, nenhum serviço externo é chamado.
/// </summary>
public sealed class TranscritorArquivo(
    IConversorMidia conversor,
    IMotorTranscricao motor,
    ICacheResultado cache,
    IOptions<OpcoesArquivo> opcoes) : ITranscritorArquivo
{
    private readonly OpcoesArquivo _opcoes = opcoes.Value;

    public async Task<ResultadoTranscricao> ProcessarAsync(
        string caminhoArquivo,
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
        var emCache = await cache.LerAsync(chave, cancellationToken).ConfigureAwait(false);
        if (emCache is not null)
        {
            await GravarAoLadoAsync(caminho, emCache, cancellationToken).ConfigureAwait(false);
            progresso?.Report(new(EtapaProcessamento.Concluido, 100, DoCache: true));
            return emCache;
        }

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
            var resultado = new ResultadoTranscricao(new InfoArquivo(hash, duracao, Idiomas.Normalizar(bruta.Idioma)), frases);

            progresso?.Report(new(EtapaProcessamento.GravandoResultado));
            await cache.GravarAsync(chave, resultado, cancellationToken).ConfigureAwait(false);
            await GravarAoLadoAsync(caminho, resultado, cancellationToken).ConfigureAwait(false);

            progresso?.Report(new(EtapaProcessamento.Concluido, 100));
            return resultado;
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
    }

    /// <summary>Hash do arquivo + motor/modelo + idioma forçado: trocar o modelo não reaproveita resultado de outro.</summary>
    internal string ChaveCache(string hash)
    {
        var idioma = string.IsNullOrWhiteSpace(_opcoes.Idioma) ? "auto" : Idiomas.Normalizar(_opcoes.Idioma);
        return $"{hash}.{motor.Identificador}.{idioma}".Replace('/', '_');
    }

    private async Task GravarAoLadoAsync(string caminho, ResultadoTranscricao resultado, CancellationToken cancellationToken)
    {
        if (_opcoes.GravarJsonAoLado)
        {
            await JsonTranscricao.GravarAsync(resultado, OpcoesArquivo.CaminhoJsonAoLado(caminho), cancellationToken).ConfigureAwait(false);
        }
    }

    private static double? Percentual(double segundos, double duracao) =>
        duracao > 0 ? Math.Clamp(segundos * 100 / duracao, 0, 100) : null;
}
