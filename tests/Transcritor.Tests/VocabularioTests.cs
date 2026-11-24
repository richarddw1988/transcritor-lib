using Transcritor.Arquivo;
using Transcritor.Arquivo.Cache;
using Transcritor.Arquivo.Midia;
using Transcritor.Core;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public sealed class VocabularioTests : IDisposable
{
    private readonly PastaTemporaria _pasta = new();

    public void Dispose() => _pasta.Dispose();

    private static TranscricaoBruta Bruta(string idioma) =>
        new(idioma, 4, [new SegmentoBruto(0, 1, "oi")], [new Palavra("oi", 0, 1, 0.9)]);

    private async Task<(FakeMotorSequencia Motor, TranscritorArquivo Transcritor, string Midia)> Processar(
        Action<OpcoesArquivo> ajustar, string? vocabularioAoLado = null, double duracao = 8)
    {
        var opcoes = Opcoes.Arquivo(o =>
        {
            o.PastaCache = Path.Combine(_pasta.Caminho, "cache");
            o.RepararTrechosSuspeitos = false;
            ajustar(o);
        });
        var midia = _pasta.Arquivo("aula.wav", "x");
        if (vocabularioAoLado is not null)
        {
            await File.WriteAllTextAsync(OpcoesArquivo.CaminhoVocabularioAoLado(midia), vocabularioAoLado);
        }

        var motor = new FakeMotorSequencia(Bruta("pt"));
        var conversor = new FakeConversor(new InfoMidia(duracao, "wav", 1), ReparadorTrechosTests.Wav((int)(16000 * duracao)));
        var transcritor = new TranscritorArquivo(conversor, motor, new CacheEmDisco(opcoes), opcoes);
        await transcritor.ProcessarAsync(midia, Saida.Portugues);
        return (motor, transcritor, midia);
    }

    [Fact]
    public void Monta_termos_separados_por_linha_virgula_ou_ponto_e_virgula_sem_repetir()
    {
        var dica = Vocabulario.Montar("Dona Chica\n# comentário\nLuis Barcellos, NAudio; naudio\n\n", "Speaches.");

        Assert.Equal("Dona Chica, Luis Barcellos, NAudio, Speaches.", dica);
    }

    [Fact]
    public void Sem_termos_e_nulo()
    {
        Assert.Null(Vocabulario.Montar(null, "  \n# só comentário\n , ;"));
    }

    [Fact]
    public void Corta_no_limite_sem_partir_termo()
    {
        var termos = Enumerable.Range(0, 200).Select(i => $"termo{i:D3}");

        var dica = Vocabulario.Montar(string.Join('\n', termos))!;

        Assert.True(dica.Length <= Vocabulario.MaximoCaracteres);
        Assert.EndsWith(".", dica, StringComparison.Ordinal);
        Assert.All(dica.TrimEnd('.').Split(", "), t => Assert.Matches(@"^termo\d{3}$", t));
    }

    [Fact]
    public async Task Opcao_e_arquivo_ao_lado_vao_juntos_como_prompt()
    {
        var (motor, _, _) = await Processar(o => { o.Idioma = "pt"; o.Vocabulario = "Dona Chica, NAudio"; }, "Barcellos\nNAudio");

        Assert.Equal(["Dona Chica, NAudio, Barcellos."], motor.Contextos);
    }

    [Fact]
    public async Task Sem_ler_ao_lado_ignora_o_arquivo()
    {
        var (motor, _, _) = await Processar(o => { o.Idioma = "pt"; o.LerVocabularioAoLado = false; }, "Barcellos");

        Assert.Equal([null], motor.Contextos);
    }

    [Fact]
    public async Task Conferencia_do_idioma_roda_sem_dica_e_so_a_transcricao_final_leva()
    {
        // Arquivo curto (cabe na amostra): sem vocabulário seria uma chamada só; com ele, a final é refeita com a dica.
        var (motor, _, _) = await Processar(_ => { }, "Barcellos", duracao: 8);

        Assert.Equal([null, "en", "pt"], motor.Idiomas);
        Assert.Equal([null, null, "Barcellos."], motor.Contextos);
    }

    [Fact]
    public async Task Chave_do_cache_muda_com_o_vocabulario()
    {
        var (_, transcritor, _) = await Processar(o => o.Idioma = "pt");

        Assert.DoesNotContain(".v", transcritor.ChaveCache("abc"), StringComparison.Ordinal);
        Assert.NotEqual(transcritor.ChaveCache("abc", "A."), transcritor.ChaveCache("abc", "B."));
        Assert.Contains(".v", transcritor.ChaveCache("abc", "A."), StringComparison.Ordinal);
    }

    [Fact]
    public void Trecho_refeito_leva_vocabulario_e_o_texto_de_antes()
    {
        Assert.Equal("Barcellos. e então o", Vocabulario.ComContexto("Barcellos.", "e então o"));
        Assert.Equal("e então o", Vocabulario.ComContexto(null, "e então o"));
        Assert.Equal("Barcellos.", Vocabulario.ComContexto("Barcellos.", null));
    }
}
