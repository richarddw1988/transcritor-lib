using Transcritor.Arquivo;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public class MontadorFrasesTests
{
    /// <summary>Palavras com 0,3 s cada, sem pausa, a partir de <paramref name="inicio"/>.</summary>
    private static List<Palavra> Palavras(double inicio, params string[] textos) =>
        textos.Select((t, i) => new Palavra(" " + t, inicio + (i * 0.3), inicio + (i * 0.3) + 0.3)).ToList();

    private static TranscricaoBruta Bruta(IEnumerable<Palavra> palavras, params SegmentoBruto[] segmentos) =>
        new("en", 100, segmentos, palavras.ToList());

    private static List<string> Textos(TranscricaoBruta bruta) => MontadorFrases.Montar(bruta).Select(f => f.Texto).ToList();

    [Fact]
    public void Junta_pedacos_de_segmentos_ate_o_fim_da_sentenca()
    {
        // O Whisper cortou a sentença em dois segmentos (caso real do audiolivro).
        var p = Palavras(0, "an", "enterprise", "which", "you", "have", "regarded.", "I", "arrived.");
        var bruta = Bruta(p, new SegmentoBruto(0, 0.6, " an enterprise"), new SegmentoBruto(0.6, 2.4, " which you have regarded. I arrived."));

        var frases = MontadorFrases.Montar(bruta);

        Assert.Equal(["an enterprise which you have regarded.", "I arrived."], frases.Select(f => f.Texto));
        Assert.Equal(6, frases[0].Palavras.Count);
        Assert.Equal(0, frases[0].Inicio);
        Assert.Equal(1.8, frases[0].Fim);
        Assert.Equal(1.8, frases[1].Inicio);
        Assert.Null(frases[0].Traducao);
    }

    [Fact]
    public void Tira_espacos_das_palavras()
    {
        var frase = Assert.Single(MontadorFrases.Montar(Bruta(Palavras(12.4, "The", "mind", "behind", "Linux"))));

        Assert.Equal("The mind behind Linux", frase.Texto);
        Assert.Equal(["The", "mind", "behind", "Linux"], frase.Palavras.Select(p => p.Texto));
        Assert.Equal(12.4, frase.Inicio);
    }

    [Theory]
    [InlineData("To Mrs. Saville, England.")]
    [InlineData("St. Petersburg, December 11th.")]
    [InlineData("O Sr. Silva chegou.")]
    [InlineData("A Dra. Ana atendeu.")]
    [InlineData("J. R. R. Tolkien wrote it.")]
    [InlineData("Use tools, e.g. hammers.")]
    [InlineData("It costs 3.5 dollars.")]
    public void Nao_corta_em_abreviacao_inicial_ou_numero(string sentenca)
    {
        Assert.Equal([sentenca], Textos(Bruta(Palavras(0, sentenca.Split(' ')))));
    }

    [Theory]
    [InlineData("I said no. Then I left.", "I said no.", "Then I left.")]
    [InlineData("Fomos ao mar. Estava frio.", "Fomos ao mar.", "Estava frio.")]
    [InlineData("Letter 1. To Mrs. Saville.", "Letter 1.", "To Mrs. Saville.")]
    [InlineData("Really? Yes!", "Really?", "Yes!")]
    [InlineData("Ele disse \"oi.\" Depois saiu.", "Ele disse \"oi.\"", "Depois saiu.")]
    public void Corta_no_fim_de_cada_sentenca(string texto, string primeira, string segunda)
    {
        Assert.Equal([primeira, segunda], Textos(Bruta(Palavras(0, texto.Split(' ')))));
    }

    [Fact]
    public void Pausa_longa_fecha_a_frase_mesmo_sem_pontuacao()
    {
        var p = Palavras(0, "sem", "pontuacao").Concat(Palavras(3, "outra", "fala")).ToList();

        Assert.Equal(["sem pontuacao", "outra fala"], Textos(Bruta(p)));
    }

    [Fact]
    public void Titulo_com_pausa_antes_do_numero_fica_junto_e_sozinho()
    {
        // Audiolivro: "Chapter" [2 s] "Three It was at this point..." (sem ponto depois de "Three").
        var p = Palavras(0, "the", "end.").Concat(Palavras(3, "Chapter")).Concat(Palavras(5.5, "Three", "It", "was", "late."))
            .ToList();

        Assert.Equal(["the end.", "Chapter Three", "It was late."], Textos(Bruta(p)));
    }

    [Theory]
    [InlineData("Chapter 2 It was late.", "Chapter 2")]
    [InlineData("Capítulo três Era tarde.", "Capítulo três")]
    [InlineData("Part IV It was late.", "Part IV")]
    [InlineData("Chapter Twenty-One It was late.", "Chapter Twenty-One")]
    public void Titulo_sem_pontuacao_fecha_antes_da_maiuscula(string texto, string titulo)
    {
        var frases = Textos(Bruta(Palavras(0, texto.Split(' '))));

        Assert.Equal(titulo, frases[0]);
        Assert.Equal(2, frases.Count);
    }

    [Theory]
    [InlineData("Chapter three of the book was long.")]
    [InlineData("It was part one of the plan.")]
    public void Titulo_seguido_de_minuscula_ou_sem_maiuscula_nao_corta(string texto) =>
        Assert.Single(Textos(Bruta(Palavras(0, texto.Split(' ')))));

    [Fact]
    public void Sentenca_longa_e_dividida_na_ultima_virgula_que_cabe()
    {
        // 150 palavras de 0,3 s = 45 s, com uma vírgula aos ~15 s e outra aos ~27 s.
        var textos = Enumerable.Range(0, 150).Select(i => i switch { 49 => "a,", 89 => "b,", 149 => "fim.", _ => "x" }).ToArray();

        var frases = MontadorFrases.Montar(Bruta(Palavras(0, textos)));

        Assert.Equal(2, frases.Count);
        Assert.EndsWith("b,", frases[0].Texto, StringComparison.Ordinal);
        Assert.True(frases[0].Fim - frases[0].Inicio <= MontadorFrases.DuracaoMaxima.TotalSeconds);
        Assert.EndsWith("fim.", frases[1].Texto, StringComparison.Ordinal);
        Assert.Equal(150, frases.Sum(f => f.Palavras.Count));
    }

    [Fact]
    public void Sem_tempo_por_palavra_usa_os_segmentos()
    {
        var bruta = new TranscricaoBruta("en", 5, [new SegmentoBruto(1, 2, " Music"), new SegmentoBruto(3, 4, " ")], []);

        var frase = Assert.Single(MontadorFrases.Montar(bruta));
        Assert.Equal("Music", frase.Texto);
        Assert.Empty(frase.Palavras);
        Assert.Equal(1, frase.Inicio);
    }

    [Fact]
    public void Arredonda_tempos_em_milissegundos()
    {
        var palavra = MontadorFrases.Montar(Bruta([new Palavra(" x", 0.12345, 0.67891)]))[0].Palavras[0];
        Assert.Equal(0.123, palavra.Inicio);
        Assert.Equal(0.679, palavra.Fim);
    }
}
