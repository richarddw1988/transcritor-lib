using Transcritor.Core;
using Transcritor.Core.Erros;

namespace Transcritor.Tests;

public class RegraSaidaTests
{
    [Theory]
    [InlineData(Saida.Ambos, "pt", "en")]
    [InlineData(Saida.Ambos, "en", "pt")]
    [InlineData(Saida.Portugues, "en", "pt")]
    [InlineData(Saida.Ingles, "pt", "en")]
    [InlineData(Saida.Portugues, "pt", null)]
    [InlineData(Saida.Ingles, "en", null)]
    [InlineData(Saida.Ingles, "english", null)]
    [InlineData(Saida.Portugues, "es", "pt")]
    public void Decide_o_idioma_da_traducao(Saida saida, string falado, string? esperado) =>
        Assert.Equal(esperado, RegraSaida.IdiomaDaTraducao(saida, falado));

    [Fact]
    public void Ambos_com_outro_idioma_falado_gera_erro_claro()
    {
        var ex = Assert.Throws<TranscritorException>(() => RegraSaida.IdiomaDaTraducao(Saida.Ambos, "es"));
        Assert.Contains("'es'", ex.Message, StringComparison.Ordinal);
    }
}
