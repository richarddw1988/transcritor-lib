using Transcritor.Arquivo;
using Transcritor.Core.Modelos;

namespace Transcritor.Tests;

public class MontadorFrasesTests
{
    [Fact]
    public void Associa_palavras_ao_segmento_e_tira_espacos()
    {
        var bruta = new TranscricaoBruta("en", 10,
            [new SegmentoBruto(0, 2, " The mind behind Linux"), new SegmentoBruto(2.5, 4, " Hello")],
            [
                new Palavra(" The", 0.0, 0.2), new Palavra(" mind", 0.2, 0.6),
                new Palavra(" behind", 0.6, 1.1), new Palavra(" Linux", 1.1, 1.9),
                new Palavra(" Hello", 2.6, 3.2),
            ]);

        var frases = MontadorFrases.Montar(bruta);

        Assert.Equal(2, frases.Count);
        Assert.Equal("The mind behind Linux", frases[0].Texto);
        Assert.Equal(["The", "mind", "behind", "Linux"], frases[0].Palavras.Select(p => p.Texto));
        Assert.Equal(0.0, frases[0].Inicio);
        Assert.Equal(1.9, frases[0].Fim);
        Assert.Null(frases[0].Traducao);
        Assert.Equal("Hello", Assert.Single(frases[1].Palavras).Texto);
        Assert.Equal(2.6, frases[1].Inicio);
    }

    [Fact]
    public void Divide_segmento_com_duas_frases()
    {
        var bruta = new TranscricaoBruta("pt", 5,
            [new SegmentoBruto(0, 3, " Olá. Tudo bem?")],
            [new Palavra(" Olá.", 0, 0.5), new Palavra(" Tudo", 0.8, 1.1), new Palavra(" bem?", 1.1, 1.6)]);

        var frases = MontadorFrases.Montar(bruta);

        Assert.Equal(["Olá.", "Tudo bem?"], frases.Select(f => f.Texto));
        Assert.Equal(0.8, frases[1].Inicio);
        Assert.Equal(1.6, frases[1].Fim);
    }

    [Fact]
    public void Nao_divide_quando_pontuacao_so_no_fim()
    {
        var bruta = new TranscricaoBruta("pt", 5,
            [new SegmentoBruto(0, 3, " Ele disse \"oi.\"")],
            [new Palavra(" Ele", 0, 0.3), new Palavra(" disse", 0.3, 0.7), new Palavra(" \"oi.\"", 0.7, 1.0)]);

        var frase = Assert.Single(MontadorFrases.Montar(bruta));
        Assert.Equal("Ele disse \"oi.\"", frase.Texto);
    }

    [Fact]
    public void Segmento_sem_palavras_vira_frase_sem_palavras()
    {
        var bruta = new TranscricaoBruta("en", 5, [new SegmentoBruto(1, 2, " Music")], []);

        var frase = Assert.Single(MontadorFrases.Montar(bruta));
        Assert.Equal("Music", frase.Texto);
        Assert.Empty(frase.Palavras);
        Assert.Equal(1, frase.Inicio);
    }

    [Fact]
    public void Palavra_na_fronteira_vai_para_o_segmento_do_meio_dela()
    {
        var bruta = new TranscricaoBruta("en", 5,
            [new SegmentoBruto(0, 1.0, " a b"), new SegmentoBruto(1.0, 2.0, " c")],
            [new Palavra(" a", 0, 0.4), new Palavra(" b", 0.4, 0.98), new Palavra(" c", 0.96, 1.5)]);

        var frases = MontadorFrases.Montar(bruta);

        Assert.Equal(["a", "b"], frases[0].Palavras.Select(p => p.Texto));
        Assert.Equal(["c"], frases[1].Palavras.Select(p => p.Texto));
    }

    [Fact]
    public void Arredonda_tempos_em_milissegundos()
    {
        var bruta = new TranscricaoBruta("en", 5,
            [new SegmentoBruto(0, 1, " x")],
            [new Palavra(" x", 0.12345, 0.67891)]);

        var palavra = MontadorFrases.Montar(bruta)[0].Palavras[0];
        Assert.Equal(0.123, palavra.Inicio);
        Assert.Equal(0.679, palavra.Fim);
    }
}
