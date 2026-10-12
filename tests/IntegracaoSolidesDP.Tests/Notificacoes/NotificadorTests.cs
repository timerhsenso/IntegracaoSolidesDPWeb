using System.Text;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Notificacoes;

public sealed class NotificadorTests
{
    private readonly MemoryNotificacaoStore _store = new();
    private readonly FakeEnviadorEmail _enviador = new();

    // Segunda-feira, 07:00 em Salvador.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.FromHours(-3)));

    public NotificadorTests()
    {
        _store.Configuracao = new ConfiguracaoNotificacao
        {
            Ativo = true,
            Servidor = "smtp.teste.com.br",
            Porta = 587,
            Usuario = "avisos@adn.com.br",
            SenhaCifrada = Encoding.UTF8.GetBytes("segredo"),
            Remetente = "avisos@adn.com.br",
            UrlWeb = "https://srv:8443/",
        };
        _store.Destinatarios.Add(new DestinatarioNotificacao { Id = 1, Email = "ti@adn.com.br", RecebeFalhas = true });
        _store.Destinatarios.Add(new DestinatarioNotificacao { Id = 2, Email = "rh15@adn.com.br", Cdempresa = 15, RecebeResumo = true });
        _store.Destinatarios.Add(new DestinatarioNotificacao { Id = 3, Email = "rh14@adn.com.br", Cdempresa = 14, RecebeResumo = true });
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Weekly_conference_with_differences_goes_to_who_receives_the_summary_of_that_company()
    {
        var conferencia = new Conferencia
        {
            Id = 7, Instancia = "default", Cdempresa = 15, IniciadaEm = _clock.GetUtcNow(), ConcluidaEm = _clock.GetUtcNow(),
            Gatilho = GatilhosConferencia.Agendada, Conferidos = 33, Pessoas = 1, Divergencias = 1,
            Linhas = [new DivergenciaCampo { Rotulo = "15-00000001", Cpf = TestData.Cpf1, Campo = CamposConferencia.Cargo, RhSenso = "00200", Dp = "00100" }],
        };

        await Criar().AvisarConferenciaAsync(conferencia, Ct);
        await Criar().AvisarConferenciaAsync(conferencia with { Pessoas = 0, Divergencias = 0, Linhas = [] }, Ct);

        var email = _enviador.Enviados.Should().ContainSingle("sem diferença não há e-mail").Subject.Mensagem;
        email.Para.Should().Equal("rh15@adn.com.br");
        email.Html.Should().Contain("15-00000001").And.Contain("https://srv:8443/Conferencia/Detalhe/7");
    }

    [Fact]
    public async Task Failure_is_sent_once_reminded_after_the_interval_and_recovery_is_announced()
    {
        var notificador = Criar();

        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Failed, erro: "Sólides DP recusou o token (HTTP 401)"), [], Ct);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Failed, erro: "Sólides DP recusou o token (HTTP 401)"), [], Ct);

        var primeiro = _enviador.Enviados.Should().ContainSingle("a mesma falha a cada 30 minutos não repete o e-mail").Subject;
        primeiro.Mensagem.Para.Should().Equal("ti@adn.com.br");
        primeiro.Mensagem.Assunto.Should().Contain("Empresa 15").And.Contain("Falhou");
        // Acentos vão como entidades HTML (&#243;): as conferências usam trechos sem acento.
        primeiro.Mensagem.Html.Should().Contain("recusou o token").And.Contain("https://srv:8443/Execucoes/Detalhe/");
        primeiro.Smtp.Senha.Should().Be("segredo");

        _clock.Advance(TimeSpan.FromHours(24));
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithErrors),
            [new RunItem(EntityTypes.Employee, "15-00000001", ItemActions.Create, ItemStatuses.Failed, 400, "CPF invalido na Receita")], Ct);

        _enviador.Enviados.Should().HaveCount(2);
        _enviador.Enviados[1].Mensagem.Assunto.Should().Contain("continua com problema desde 05/10/2026 07:00");
        _enviador.Enviados[1].Mensagem.Html.Should().Contain("CPF invalido na Receita");

        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Completed), [], Ct);
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Completed), [], Ct);

        _enviador.Enviados.Should().HaveCount(3);
        _enviador.Enviados[2].Mensagem.Assunto.Should().Contain("voltou ao normal");
        _store.Envios.Select(e => e.Tipo).Should().Equal(TiposNotificacao.Falha, TiposNotificacao.Lembrete, TiposNotificacao.Normalizou);
        _store.Envios.Should().OnlyContain(e => e.Ok);
    }

    [Fact]
    public async Task Pending_items_and_simulations_do_not_send_right_away()
    {
        var notificador = Criar();

        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending), [Bloqueado("15-00901288", "cpf_desligado_no_dp: desligado")], Ct);
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Failed, dryRun: true, erro: "token"), [], Ct);

        _enviador.Enviados.Should().BeEmpty("pendência vai no resumo diário e simulação não avisa por padrão");
    }

    [Fact]
    public async Task Daily_summary_goes_once_a_day_with_only_the_new_pending_items_of_each_recipient_company()
    {
        var notificador = Criar();
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending),
            [Bloqueado("15-00901288", "cpf_desligado_no_dp: DIANA"), new RunItem(EntityTypes.Employee, "15-00000002", ItemActions.None, ItemStatuses.Skipped, Message: "superseded: linha duplicada")], Ct);
        await notificador.AposExecucaoAsync(Execucao(14, RunStatuses.CompletedWithPending), [Bloqueado("14-00000001", "company_not_found: CNPJ")], Ct);

        await notificador.ResumoSeDevidoAsync(Ct);
        _enviador.Enviados.Should().BeEmpty("antes das 08:00");

        _clock.Advance(TimeSpan.FromHours(1));
        await notificador.ResumoSeDevidoAsync(Ct);
        await notificador.ResumoSeDevidoAsync(Ct);

        _enviador.Enviados.Should().HaveCount(2, "um e-mail por destinatário, uma vez por dia");
        var rh15 = _enviador.Enviados.Single(e => e.Mensagem.Para.Contains("rh15@adn.com.br")).Mensagem;
        rh15.Assunto.Should().Contain("1 nova(s), 1 em aberto");
        rh15.Html.Should().Contain("15-00901288").And.Contain("DIANA").And.NotContain("superseded").And.NotContain("14-00000001");

        _clock.Advance(TimeSpan.FromDays(1));
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending),
            [Bloqueado("15-00901288", "cpf_desligado_no_dp: DIANA"), Bloqueado("15-00901970", "cpf_desligado_no_dp: DORFINO")], Ct);
        await notificador.ResumoSeDevidoAsync(Ct);

        _enviador.Enviados.Should().HaveCount(3, "a empresa 14 não tem nada novo");
        var terca = _enviador.Enviados[2].Mensagem;
        terca.Para.Should().Equal("rh15@adn.com.br");
        terca.Assunto.Should().Contain("1 nova(s), 2 em aberto");
        terca.Html.Should().Contain("DORFINO").And.NotContain("DIANA");
    }

    [Fact]
    public async Task Resolved_pending_item_that_comes_back_counts_as_new()
    {
        var notificador = Criar();
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending), [Bloqueado("15-00901288", "cpf_desligado_no_dp: DIANA")], Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await notificador.ResumoSeDevidoAsync(Ct);

        _clock.Advance(TimeSpan.FromHours(2));
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.Completed), [], Ct);
        (await _store.PendenciasAbertasAsync(Ct)).Should().BeEmpty("não apareceu na execução: resolvida");

        _clock.Advance(TimeSpan.FromHours(22)); // terça, 08:00
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending), [Bloqueado("15-00901288", "cpf_desligado_no_dp: DIANA")], Ct);
        await notificador.ResumoSeDevidoAsync(Ct);

        _enviador.Enviados.Should().HaveCount(2);
        _enviador.Enviados[1].Mensagem.Html.Should().Contain("DIANA");
    }

    [Fact]
    public async Task No_summary_on_weekends_unless_configured()
    {
        _clock.Advance(TimeSpan.FromDays(5) + TimeSpan.FromHours(2)); // sábado, 09:00
        var notificador = Criar();
        await notificador.AposExecucaoAsync(Execucao(15, RunStatuses.CompletedWithPending), [Bloqueado("15-00901288", "cpf_desligado_no_dp: DIANA")], Ct);

        await notificador.ResumoSeDevidoAsync(Ct);
        _enviador.Enviados.Should().BeEmpty();

        _store.Configuracao = _store.Configuracao! with { SomenteDiasUteis = false };
        await notificador.ResumoSeDevidoAsync(Ct);
        _enviador.Enviados.Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_happens_without_web_management_or_with_notifications_off()
    {
        await Criar(gestao: false).AposExecucaoAsync(Execucao(15, RunStatuses.Failed, erro: "x"), [], Ct);
        _store.Configuracao = _store.Configuracao! with { Ativo = false };
        await Criar().AposExecucaoAsync(Execucao(15, RunStatuses.Failed, erro: "x"), [], Ct);

        _enviador.Enviados.Should().BeEmpty();
        _store.Envios.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_email_goes_to_every_active_recipient_and_reports_the_server_error()
    {
        var notificador = Criar();
        _store.Configuracao = _store.Configuracao! with { Ativo = false };

        var resultado = await notificador.EnviarTesteAsync(Ct);

        resultado.Should().Contain("ti@adn.com.br").And.Contain("rh15@adn.com.br").And.Contain("rh14@adn.com.br");
        _enviador.Enviados.Should().ContainSingle().Which.Mensagem.Para.Should().HaveCount(3);

        _enviador.Falhar = "5.7.8 Authentication failed";
        var falha = async () => await notificador.EnviarTesteAsync(Ct);

        (await falha.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Authentication failed*");
        _store.Envios.Should().HaveCount(2);
        _store.Envios[1].Ok.Should().BeFalse();
        _store.Envios[1].Erro.Should().Contain("Authentication failed");
    }

    [Fact]
    public void Pending_key_uses_the_reason_code_so_the_same_problem_keeps_the_same_key()
    {
        var pendencias = Notificador.Pendencias(
        [
            Bloqueado("15-1", "cpf_desligado_no_dp: este CPF está DESLIGADO (cadastro 1)"),
            Bloqueado("15-2", "cargo 00100 indisponível no DP"),
            new RunItem(EntityTypes.Employee, "15-3", ItemActions.None, ItemStatuses.Skipped, Message: "cpf_invalido: CPF vazio"),
            new RunItem(EntityTypes.Employee, "15-4", ItemActions.None, ItemStatuses.Skipped, Message: "fora_do_escopo: filial 2"),
            new RunItem(EntityTypes.Employee, "15-5", ItemActions.Update, ItemStatuses.Failed, Message: "erro da API"),
        ]);

        pendencias.Select(p => p.Chave).Should().Equal(
            "employee|15-1|cpf_desligado_no_dp",
            "employee|15-2|cargo 00100 indisponível no DP",
            "employee|15-3|cpf_invalido");
    }

    private Notificador Criar(bool gestao = true) => new(
        _store,
        _enviador,
        new TextoComoSenha(),
        Options.Create(new ManagementOptions { Habilitada = gestao }),
        TestData.Dates,
        _clock,
        NullLogger<Notificador>.Instance);

    private RunSummary Execucao(int empresa, string status, bool dryRun = false, string? erro = null) =>
        new(Guid.NewGuid(), status, dryRun, _clock.GetUtcNow(), _clock.GetUtcNow(),
            new Dictionary<string, IReadOnlyDictionary<string, int>>(), erro, null, empresa);

    private static RunItem Bloqueado(string rotulo, string mensagem) =>
        new(EntityTypes.Employee, rotulo, ItemActions.None, ItemStatuses.Blocked, Message: mensagem);

    /// <summary>A cifra de verdade tem testes próprios.</summary>
    private sealed class TextoComoSenha : ITokenProtector
    {
        public byte[] Protect(string token) => Encoding.UTF8.GetBytes(token);

        public string Unprotect(byte[] protegido) => Encoding.UTF8.GetString(protegido);
    }
}
