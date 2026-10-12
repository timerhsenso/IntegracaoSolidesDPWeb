using System.Net;
using IntegracaoSolidesDP.Tests.EndToEnd;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Tela Notificações: só Admin, senha do SMTP cifrada e nunca exibida, destinatários e o teste como pedido ao serviço.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class NotificacoesWebTests(SqlServerFixture db) : IAsyncLifetime
{
    private const string Pagina = "/Notificacoes";
    private WebHarness _web = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Admin_configures_the_smtp_with_an_encrypted_password_that_is_never_shown()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);

        var salvar = await WebHarness.PostFormAsync(client, Pagina, "/Notificacoes/Salvar", new()
        {
            ["Form.Ativo"] = "true",
            ["Form.Servidor"] = "smtp.skymail.net.br",
            ["Form.Porta"] = "587",
            ["Form.Usuario"] = "adnserv@adn.com.br",
            ["Form.Senha"] = "segredo123",
            ["Form.Remetente"] = "adnserv@adn.com.br",
            ["Form.StartTls"] = "true",
            ["Form.HoraResumo"] = "07:30",
            ["Form.SomenteDiasUteis"] = "true",
            ["Form.LembreteHoras"] = "24",
            ["Form.UrlWeb"] = "https://srvhomologacao:8443",
        });

        salvar.StatusCode.Should().Be(HttpStatusCode.Redirect, await salvar.Content.ReadAsStringAsync(Ct));
        var config = (await new SqlNotificacaoStore(db.Connections, TimeProvider.System).ConfiguracaoAsync(Ct))!;
        config.Servidor.Should().Be("smtp.skymail.net.br");
        config.HoraResumo.Should().Be(new TimeSpan(7, 30, 0));
        config.SenhaCifrada.Should().NotBeNull();
        new TokenProtector(new TokenProtectionOptions { ChaveBase64 = E2EHarness.ChaveTokens }).Unprotect(config.SenhaCifrada!)
            .Should().Be("segredo123");

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Pagina, Ct));
        html.Should().NotContain("segredo123").And.Contain("(gravada)").And.Contain("value=\"07:30\"");

        // Gravar de novo sem senha mantém a senha.
        await WebHarness.PostFormAsync(client, Pagina, "/Notificacoes/Salvar", new()
        {
            ["Form.Servidor"] = "smtp.skymail.net.br",
            ["Form.Porta"] = "587",
            ["Form.Remetente"] = "adnserv@adn.com.br",
            ["Form.HoraResumo"] = "08:00",
            ["Form.LembreteHoras"] = "24",
        });
        (await new SqlNotificacaoStore(db.Connections, TimeProvider.System).ConfiguracaoAsync(Ct))!.SenhaCifrada.Should().Equal(config.SenhaCifrada);
        (await Auditoria(login)).Should().Contain(AcoesAuditoria.NotificacaoAlterada);
        (await db.QueryAsync<string>("SELECT ISNULL(detalhe, '') FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().OnlyContain(d => !d.Contains("segredo123", StringComparison.Ordinal), "a senha nunca vai para a auditoria");
    }

    [Fact]
    public async Task Admin_adds_and_deactivates_recipients_and_asks_the_service_for_a_test_email()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);

        var incluir = await WebHarness.PostFormAsync(client, Pagina, "/Notificacoes/Destinatario", new()
        {
            ["NovoDestinatario.Email"] = "rh15@adn.com.br",
            ["NovoDestinatario.Cdempresa"] = "15",
            ["NovoDestinatario.RecebeFalhas"] = "false",
            ["NovoDestinatario.RecebeResumo"] = "true",
        });
        var repetido = await WebHarness.PostFormAsync(client, Pagina, "/Notificacoes/Destinatario", new()
        {
            ["NovoDestinatario.Email"] = "RH15@adn.com.br",
            ["NovoDestinatario.Cdempresa"] = "15",
            ["NovoDestinatario.RecebeFalhas"] = "false",
            ["NovoDestinatario.RecebeResumo"] = "true",
        });

        incluir.StatusCode.Should().Be(HttpStatusCode.Redirect);
        repetido.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var store = new SqlNotificacaoStore(db.Connections, TimeProvider.System);
        var destinatario = (await store.DestinatariosAsync(Ct)).Should().ContainSingle("o mesmo e-mail para a mesma empresa não entra duas vezes").Subject;
        destinatario.Should().Match<DestinatarioNotificacao>(d => d.Cdempresa == 15 && d.RecebeResumo && !d.RecebeFalhas && d.Ativo);

        await WebHarness.PostFormAsync(client, Pagina, $"/Notificacoes/AlternarDestinatario/{destinatario.Id}", []);
        (await store.DestinatariosAsync(Ct)).Single().Ativo.Should().BeFalse();

        var teste = await WebHarness.PostFormAsync(client, Pagina, "/Notificacoes/Testar", []);

        teste.Headers.Location!.ToString().Should().Contain("/Comandos/Detalhe/");
        (await db.QueryAsync<string>("SELECT tipo FROM solidesdp.comando")).Should().Equal(CommandTypes.TestarEmail);
        (await Auditoria(login)).Should().Contain(AcoesAuditoria.NotificacaoDestinatario).And.Contain(AcoesAuditoria.ComandoSolicitado);
    }

    [Fact]
    public async Task Only_admin_opens_the_notifications_screen()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Operador));

        var resposta = await client.GetAsync(Pagina, Ct);

        resposta.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
    }

    private Task<IReadOnlyList<string>> Auditoria(string login) =>
        db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login });
}
