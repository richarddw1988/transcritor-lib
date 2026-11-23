using Microsoft.Extensions.Options;
using Transcritor.Traducao;
using Transcritor.Traducao.Correcoes;

namespace Transcritor.Tests;

/// <summary>Só roda com os modelos convertidos em dados/bert (gerados por scripts/preparar-ambiente.ps1); senão, é pulado.</summary>
public sealed class FactComBertAttribute : FactAttribute
{
    public FactComBertAttribute()
    {
        if (!File.Exists(Path.Combine(EscolhedorHomofonosBertTests.Pasta, "en", "model.onnx"))
            || !File.Exists(Path.Combine(EscolhedorHomofonosBertTests.Pasta, "pt", "model.onnx")))
        {
            Skip = $"Sem os modelos BERT em {EscolhedorHomofonosBertTests.Pasta} (gere com scripts/preparar-ambiente.ps1).";
        }
    }
}

public sealed class EscolhedorHomofonosBertTests : IDisposable
{
    /// <summary>dados/bert na raiz do repositório (o teste roda em tests/Transcritor.Tests/bin/...).</summary>
    internal static readonly string Pasta = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dados", "bert"));

    private readonly EscolhedorHomofonosBert _bert = new(Options.Create(new OpcoesTraducao { PastaModelosHomofonos = Pasta }));

    public void Dispose() => _bert.Dispose();

    private double Probabilidade(string frase, int indice, string grafia, string[] grafias, string idioma, bool lacuna = false) =>
        _bert.Avaliar(frase.Split(' '), indice, grafias, idioma, lacuna).Single(g => string.Equals(g.Grafia, grafia, StringComparison.OrdinalIgnoreCase)).Probabilidade;

    [FactComBert]
    public void Mesmas_probabilidades_do_prototipo_em_python()
    {
        // A 2ª "cela" é "sela"; a 1ª fica. Com a menor das duas notas (2026-11-23) a "sela" caiu de 0,997 (protótipo em
        // PyTorch) para 0,989: vai ao modelo de conversa em vez de trocar direto.
        const string Cela = "A cela da prisão era pequena e a cela do cavalo estava rasgada.";
        Assert.Equal(0.989, Probabilidade(Cela, 8, "sela", ["cela", "sela"], "pt"), 2);
        Assert.Equal(0.957, Probabilidade(Cela, 1, "cela", ["cela", "sela"], "pt"), 2);
        Assert.Equal(0.9996, Probabilidade("There house is bigger than ours.", 0, "their", ["there", "their"], "en"), 3);
        Assert.Equal(0.9994, Probabilidade("The night rode his horse through the forest.", 1, "knight", ["night", "knight", "nite"], "en"), 3);
    }

    [FactComBert]
    public void Outra_ocorrencia_da_palavra_vira_lacuna_e_nao_e_copiada()
    {
        // Sem a lacuna, o "concerto de música" à vista segura o 1º "concerto": "conserto" fica com 0,29; com ela, 0,66
        // (vai ao modelo de conversa, que escolhe "conserto").
        const string Frase = "O concerto do carro ficou mais caro do que o concerto de música.";
        const string Cela = "A cela da prisão era pequena e a cela do cavalo estava rasgada.";

        Assert.Equal(0.286, Probabilidade(Frase, 1, "conserto", ["concerto", "conserto"], "pt"), 0.005);
        Assert.Equal(0.659, Probabilidade(Frase, 1, "conserto", ["concerto", "conserto"], "pt", lacuna: true), 0.005);
        // Com a lacuna: a 2ª "cela" continua "sela"; a 1ª fica, por pouco.
        Assert.Equal(0.991, Probabilidade(Cela, 8, "sela", ["cela", "sela"], "pt", lacuna: true), 3);
        Assert.Equal(0.525, Probabilidade(Cela, 1, "cela", ["cela", "sela"], "pt", lacuna: true), 0.005);
    }

    [FactComBert]
    public void Mantem_a_maiuscula_e_a_pontuacao_fica_fora_da_palavra()
    {
        var r = _bert.Avaliar("Until he was four years old.".Split(' '), 3, ["four", "for"], "en");

        Assert.Equal("four", r[0].Grafia);
        Assert.True(r[0].Probabilidade > 0.99);
        Assert.Equal("There", _bert.Avaliar("There was a faint rustling sound.".Split(' '), 0, ["there", "their"], "en")[0].Grafia);
    }

    [Fact]
    public void Sem_pasta_ou_sem_modelo_nao_esta_disponivel()
    {
        using var sem = new EscolhedorHomofonosBert(Options.Create(new OpcoesTraducao()));
        using var vazia = new EscolhedorHomofonosBert(Options.Create(new OpcoesTraducao { PastaModelosHomofonos = Path.GetTempPath() }));

        Assert.False(sem.Disponivel("en"));
        Assert.False(vazia.Disponivel("pt"));
    }

    [Theory]
    [InlineData("rode at night.", 2, "rode at", "night", ".")]
    [InlineData("\"Cela\", disse", 0, "\"", "Cela", "\", disse")]
    [InlineData("a cela", 1, "a", "cela", "")]
    public void Partes_separam_a_pontuacao_colada(string frase, int indice, string antes, string palavra, string depois) =>
        Assert.Equal((antes, palavra, depois), EscolhedorHomofonosBert.Partes(frase.Split(' '), indice));
}
