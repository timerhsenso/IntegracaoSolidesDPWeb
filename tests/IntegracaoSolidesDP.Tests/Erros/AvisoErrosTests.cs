using System.Text;
using IntegracaoSolidesDP.Tests.Notificacoes;
using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Erros;

/// <summary>E-mails dos erros ao suporte: só para quem recebe erros, conforme o modo, sem repetir e com limite diário.</summary>
public sealed class AvisoErrosTests
{
    private readonly MemoryNotificacaoStore _notificacoes = new();
    private readonly MemoryErroStore _erros = new();
    private readonly FakeEnviadorEmail _enviador = new();

    // Quarta-feira, 10:00 em Salvador.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(-3)));

    public AvisoErrosTests()
    {
        _notificacoes.Configuracao = new ConfiguracaoNotificacao
        {
            Ativo = true, Servidor = "smtp.teste.com.br", Porta = 587, Remetente = "avisos@adn.com.br", UrlWeb = "https://srv:8443",
        };
        _notificacoes.Destinatarios.Add(new DestinatarioNotificacao { Id = 1, Email = "ti@adn.com.br", RecebeFalhas = true, RecebeResumo = true });
        _notificacoes.Destinatarios.Add(new DestinatarioNotificacao { Id = 2, Email = "suporte@rhsenso.com.br", RecebeErros = true });
        _erros.Envios = () => _notificacoes.Envios;
        _erros.Configuracao = new ConfiguracaoErros { NomeCliente = "ADN" };
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_error_goes_once_only_to_who_receives_errors_and_a_summary_follows_if_it_keeps_happening()
    {
        _erros.Erros.Add(Erro(1, "Falha na conferência semanal: timeout", ocorrencias: 3));
        var aviso = Criar();

        await aviso.ProcessarAsync(Ct);
        await aviso.ProcessarAsync(Ct);

        var email = _enviador.Enviados.Should().ContainSingle("o mesmo erro não sai de novo").Subject.Mensagem;
        email.Para.Should().Equal("suporte@rhsenso.com.br");
        email.Assunto.Should().Be("[Integração Sólides DP · ADN] Erro E-1 no serviço: Falha na conferência semanal: timeout");
        email.Html.Should().Contain("já aconteceu 3 vezes").And.Contain("pilha do erro").And.Contain("https://srv:8443/Erros/Detalhe/1");
        _notificacoes.Envios.Should().ContainSingle().Which.Tipo.Should().Be(TiposNotificacao.Erro);

        // Continua acontecendo: o resumo só sai depois do intervalo (4 h).
        _erros.Erros[0] = _erros.Erros[0] with { Ocorrencias = 7, UltimaVez = _clock.GetUtcNow() };
        _clock.Advance(TimeSpan.FromHours(3));
        await aviso.ProcessarAsync(Ct);
        _enviador.Enviados.Should().HaveCount(1);

        _clock.Advance(TimeSpan.FromHours(1));
        await aviso.ProcessarAsync(Ct);
        var resumo = _enviador.Enviados.Should().HaveCount(2).And.Subject.Last().Mensagem;
        resumo.Assunto.Should().Be("[Integração Sólides DP · ADN] Resumo: o erro E-1 continua acontecendo");
        resumo.Html.Should().Contain("E-1");
        _erros.Erros[0].OcorrenciasNoAviso.Should().Be(7);
    }

    [Fact]
    public async Task Silenced_old_and_disabled_origin_errors_are_not_sent_automatically()
    {
        _erros.Erros.Add(Erro(1, "silenciado") with { Situacao = SituacoesErro.Silenciado });
        _erros.Erros.Add(Erro(2, "antigo") with { UltimaVez = _clock.GetUtcNow().AddDays(-2) });
        _erros.Erros.Add(Erro(3, "da web") with { Origem = OrigensErro.Web });
        _erros.Configuracao = _erros.Configuracao with { OrigemWeb = false };

        await Criar().ProcessarAsync(Ct);

        _enviador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task The_daily_limit_sends_one_last_warning_and_the_rest_waits_for_the_next_day()
    {
        _erros.Configuracao = _erros.Configuracao with { LimiteDia = 2 };
        for (var i = 1; i <= 4; i++)
        {
            _erros.Erros.Add(Erro(i, $"erro diferente {i}"));
        }

        var aviso = Criar();
        await aviso.ProcessarAsync(Ct);
        await aviso.ProcessarAsync(Ct);

        _notificacoes.Envios.Select(e => e.Tipo).Should().Equal(TiposNotificacao.Erro, TiposNotificacao.Erro, TiposNotificacao.ErroLimite);
        _enviador.Enviados.Last().Mensagem.Html.Should().Contain("2 erro(s) aguardando");

        _clock.Advance(TimeSpan.FromHours(15)); // quinta, 01:00: dia novo
        foreach (var indice in new[] { 2, 3 })
        {
            _erros.Erros[indice] = _erros.Erros[indice] with { UltimaVez = _clock.GetUtcNow() };
        }

        await aviso.ProcessarAsync(Ct);
        _notificacoes.Envios.Should().HaveCount(5);
        _erros.Erros.Should().OnlyContain(e => e.UltimoAviso != null);
    }

    [Fact]
    public async Task Manual_mode_sends_only_what_was_asked_and_disabled_sends_nothing()
    {
        _erros.Configuracao = _erros.Configuracao with { ModoEnvio = ModosEnvioErro.Manual };
        _erros.Erros.Add(Erro(1, "não pedido"));
        _erros.Erros.Add(Erro(2, "pedido") with { Situacao = SituacoesErro.Silenciado, Comentario = "carlos: salvando a empresa 15" });
        await _erros.ComentarAsync(2, "carlos", null, pedirEnvio: true, Ct);

        await Criar().ProcessarAsync(Ct);

        var email = _enviador.Enviados.Should().ContainSingle().Subject.Mensagem;
        email.Assunto.Should().Contain("E-2");
        email.Html.Should().Contain("<b>carlos</b> pediu o envio").And.Contain("salvando a empresa 15");
        _notificacoes.Envios.Single().Tipo.Should().Be(TiposNotificacao.ErroManual);
        _erros.Erros.Single(e => e.Id == 2).EnvioPedidoEm.Should().BeNull("o pedido foi atendido");

        _erros.Configuracao = _erros.Configuracao with { ModoEnvio = ModosEnvioErro.Desligado };
        await _erros.ComentarAsync(1, "carlos", null, pedirEnvio: true, Ct);
        await Criar().ProcessarAsync(Ct);
        _enviador.Enviados.Should().HaveCount(1);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_recipient_or_with_the_emails_off_and_a_refused_smtp_waits()
    {
        _erros.Erros.Add(Erro(1, "erro"));
        _notificacoes.Configuracao = _notificacoes.Configuracao! with { Ativo = false };
        var aviso = Criar();

        await aviso.ProcessarAsync(Ct);
        _notificacoes.Configuracao = _notificacoes.Configuracao with { Ativo = true };
        _notificacoes.Destinatarios.RemoveAt(1);
        await aviso.ProcessarAsync(Ct);
        _enviador.Enviados.Should().BeEmpty();

        _notificacoes.Destinatarios.Add(new DestinatarioNotificacao { Id = 3, Email = "suporte@rhsenso.com.br", RecebeErros = true });
        _enviador.Falhar = "5.7.8 Authentication failed";
        await aviso.ProcessarAsync(Ct);
        _enviador.Falhar = null;
        await aviso.ProcessarAsync(Ct);

        _notificacoes.Envios.Should().ContainSingle("depois da recusa espera 15 minutos").Which.Ok.Should().BeFalse();
        _clock.Advance(AvisoErros.PausaAposFalha);
        await aviso.ProcessarAsync(Ct);
        _enviador.Enviados.Should().ContainSingle();
        _erros.Erros.Single().UltimoAviso.Should().NotBeNull();
    }

    private ErroAplicacao Erro(long id, string mensagem, int ocorrencias = 1) => new()
    {
        Id = id,
        Origem = OrigensErro.Servico,
        Fonte = "IntegracaoSolidesDP.Worker.Scheduling.ConferenciaAgendada",
        Tipo = "System.TimeoutException",
        Mensagem = mensagem,
        Detalhe = "System.TimeoutException: pilha do erro",
        PrimeiraVez = _clock.GetUtcNow().AddMinutes(-10 + id),
        UltimaVez = _clock.GetUtcNow(),
        Ocorrencias = ocorrencias,
        Situacao = SituacoesErro.Aberto,
    };

    private AvisoErros Criar() => new(
        _erros,
        _notificacoes,
        new Notificador(_notificacoes, _enviador, new TextoPuro(), Options.Create(new ManagementOptions { Habilitada = true }),
            TestData.Dates, _clock, NullLogger<Notificador>.Instance),
        Options.Create(new ManagementOptions { Habilitada = true }),
        TestData.Dates,
        _clock,
        NullLogger<AvisoErros>.Instance);

    private sealed class TextoPuro : ITokenProtector
    {
        public byte[] Protect(string token) => Encoding.UTF8.GetBytes(token);

        public string Unprotect(byte[] protegido) => Encoding.UTF8.GetString(protegido);
    }
}
