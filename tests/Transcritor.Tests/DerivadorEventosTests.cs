using Transcritor.Core.Contratos;
using Transcritor.TempoReal.WhisperLiveKit;

namespace Transcritor.Tests;

public class DerivadorEventosTests
{
    private static LinhaWlk Fala(string texto, double inicio, double fim, string? idioma = "en") => new(1, texto, inicio, fim, idioma);

    private static LinhaWlk Silencio(double inicio, double fim) => new(-2, null, inicio, fim, null);

    private static MensagemWlk Msg(string buffer, params LinhaWlk[] linhas) => new(null, "active_transcription", null, null, linhas, buffer);

    [Fact]
    public void Linha_aberta_sem_pontuacao_so_gera_provisorio()
    {
        var d = new DerivadorEventos(null);

        var eventos = d.Processar(Msg(" are", Fala("Hello how", 0, 1)));

        var e = Assert.Single(eventos);
        Assert.Equal(TipoEvento.Provisorio, e.Tipo);
        Assert.Equal("Hello how are", e.Texto);
        Assert.Equal(1, e.Id);
        Assert.Equal("en", e.Idioma);
    }

    [Fact]
    public void Frase_fechada_por_pontuacao_vira_confirmado_e_o_resto_segue_provisorio()
    {
        var d = new DerivadorEventos(null);
        d.Processar(Msg("", Fala("Hello how", 0, 1)));

        var eventos = d.Processar(Msg(" fine", Fala("Hello how are you. I am", 0, 2)));

        Assert.Equal(2, eventos.Count);
        Assert.Equal(TipoEvento.Confirmado, eventos[0].Tipo);
        Assert.Equal("Hello how are you.", eventos[0].Texto);
        Assert.Equal(1, eventos[0].Id);
        Assert.Equal(0, eventos[0].Inicio);
        Assert.InRange(eventos[0].Fim, 1.4, 1.6);
        Assert.Equal(TipoEvento.Provisorio, eventos[1].Tipo);
        Assert.Equal("I am fine", eventos[1].Texto);
        Assert.Equal(2, eventos[1].Id);
    }

    [Fact]
    public void Pausa_fecha_a_linha_e_limpa_o_provisorio()
    {
        var d = new DerivadorEventos(null);
        d.Processar(Msg("", Fala("Hello how are you. I am", 0, 2)));

        var eventos = d.Processar(Msg("", Fala("Hello how are you. I am fine", 0, 2.5), Silencio(2.5, 3.5)));

        Assert.Equal(2, eventos.Count);
        Assert.Equal(TipoEvento.Confirmado, eventos[0].Tipo);
        Assert.Equal("I am fine", eventos[0].Texto);
        Assert.Equal(2, eventos[0].Id);
        Assert.Equal(2.5, eventos[0].Fim);
        Assert.Equal(TipoEvento.Provisorio, eventos[1].Tipo);
        Assert.Equal(string.Empty, eventos[1].Texto);
    }

    [Fact]
    public void Linha_nova_depois_da_pausa_nao_repete_as_anteriores()
    {
        var d = new DerivadorEventos(null);
        d.Processar(Msg("", Fala("One.", 0, 1), Silencio(1, 2)));

        var eventos = d.Processar(Msg("", Fala("One.", 0, 1), Silencio(1, 2), Fala("Two.", 2, 3)));

        var e = Assert.Single(eventos);
        Assert.Equal("Two.", e.Texto);
        Assert.Equal(2, e.Id);
    }

    [Fact]
    public void Mensagem_igual_nao_gera_eventos()
    {
        var d = new DerivadorEventos(null);
        var msg = Msg("x", Fala("Hello", 0, 1));
        d.Processar(msg);

        Assert.Empty(d.Processar(msg));
    }

    [Fact]
    public void Finalizar_confirma_o_que_estava_pendente()
    {
        var d = new DerivadorEventos(null);
        d.Processar(Msg("talvez", Fala("Tudo certo", 0, 1, "pt")));

        var eventos = d.Finalizar();

        Assert.Equal(TipoEvento.Confirmado, eventos[0].Tipo);
        Assert.Equal("Tudo certo", eventos[0].Texto);
        Assert.Equal("pt", eventos[0].Idioma);
        Assert.Equal(string.Empty, eventos[^1].Texto);
    }

    [Fact]
    public void Ponto_dentro_de_numero_nao_quebra_a_frase()
    {
        var d = new DerivadorEventos(null);

        var eventos = d.Processar(Msg("", Fala("Version 3.5 is out. Yes", 0, 2)));

        Assert.Equal("Version 3.5 is out.", eventos[0].Texto);
    }

    [Fact]
    public void Idioma_forcado_vale_mais_que_o_detectado()
    {
        var d = new DerivadorEventos("pt-BR");

        var eventos = d.Processar(Msg("", Fala("Olá.", 0, 1, "es")));

        Assert.Equal("pt", eventos[0].Idioma);
    }

    [Theory]
    [InlineData("0:00:03.25", 3.25)]
    [InlineData("1:02:03.50", 3723.5)]
    [InlineData("0:00:00", 0)]
    [InlineData("lixo", 0)]
    public void Le_tempo_do_servidor(string texto, double esperado) =>
        Assert.Equal(esperado, MensagemWlk.LerTempo(texto), 3);

    [Fact]
    public void Interpreta_mensagem_completa_do_servidor()
    {
        var msg = MensagemWlk.Interpretar("""
            {"status":"active_transcription","lines":[
              {"speaker":1,"text":"Hello.","start":"0:00:00.50","end":"0:00:01.20","detected_language":"en"},
              {"speaker":-2,"text":null,"start":"0:00:01.20","end":"0:00:02.00"}],
             "buffer_transcription":" world","buffer_diarization":"","buffer_translation":"","remaining_time_transcription":0.1}
            """);

        Assert.Null(msg.Tipo);
        Assert.Equal(2, msg.Linhas.Count);
        Assert.Equal(0.5, msg.Linhas[0].Inicio);
        Assert.True(msg.Linhas[1].EhSilencio);
        Assert.Equal(" world", msg.BufferTranscricao);
    }
}
