using System.Net;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

[Collection(SqlServerCollection.Name)]
public sealed class WebTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly SqlManagementStore _store = new(db.Connections, TimeProvider.System);
    private WebHarness _web = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        // As tabelas que o serviço cria (runs, entity_state...) e as da gestão.
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await _store.EnsureSchemaAsync(Ct);
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Anonymous_user_is_sent_to_the_login_page()
    {
        await _web.CriarUsuarioAsync(Perfis.Consulta);

        var response = await _web.Cliente().GetAsync("/", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Conta/Entrar");
    }

    [Fact]
    public async Task Login_opens_the_dashboard_and_is_audited()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Consulta);

        var client = await _web.EntrarAsync(login);
        var painel = await client.GetAsync("/", Ct);

        painel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await painel.Content.ReadAsStringAsync(Ct)).Should().Contain("Integração");
        (await Auditoria(login)).Should().Contain("LOGIN");
    }

    [Fact]
    public async Task Wrong_password_is_refused_and_audited()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Consulta);
        var client = _web.Cliente();

        var response = await WebHarness.PostFormAsync(client, "/Conta/Entrar", "/Conta/Entrar",
            new() { ["Usuario"] = login, ["Senha"] = "errada" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // O Razor codifica os acentos no HTML (&#xE1;): compara o texto como o navegador mostra.
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Usuário ou senha inválidos");
        (await Auditoria(login)).Should().Contain("LOGIN_FALHOU");
    }

    [Fact]
    public async Task Password_set_by_the_admin_must_be_changed_first()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Operador, deveTrocarSenha: true);
        var client = await _web.EntrarAsync(login);

        var painel = await client.GetAsync("/Execucoes", Ct);

        painel.StatusCode.Should().Be(HttpStatusCode.Redirect);
        painel.Headers.Location!.ToString().Should().Contain("/Conta/AlterarSenha");
    }

    [Fact]
    public async Task Consulta_profile_cannot_request_a_run()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var response = await WebHarness.PostFormAsync(client, "/Comandos", "/Comandos/Solicitar",
            new() { ["tipo"] = CommandTypes.Run });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.comando")).Should().Equal(0);
    }

    [Fact]
    public async Task Operador_request_goes_to_the_service_queue_with_the_user_name()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Operador);
        var client = await _web.EntrarAsync(login);

        var response = await WebHarness.PostFormAsync(client, "/Comandos", "/Comandos/Solicitar",
            new() { ["tipo"] = CommandTypes.DryRun });
        var repetido = await WebHarness.PostFormAsync(client, "/Comandos", "/Comandos/Solicitar",
            new() { ["tipo"] = CommandTypes.DryRun });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Comandos/Detalhe/");
        var fila = await db.QueryAsync<(string, string, string)>("SELECT tipo, status, solicitado_por FROM solidesdp.comando");
        fila.Should().Equal((CommandTypes.DryRun, CommandStatuses.Pending, login));
        repetido.Headers.Location!.ToString().Should().NotContain("/Detalhe/", "já existe um pedido igual aguardando o serviço");
        (await Auditoria(login)).Should().Contain("COMANDO_SOLICITADO");
    }

    [Fact]
    public async Task Disabled_integration_refuses_a_real_run_but_accepts_a_dry_run()
    {
        await _store.AddConfigurationVersionAsync("default", active: false, "{}", "pausa", "carlos", Ct);
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Operador));

        await WebHarness.PostFormAsync(client, "/Comandos", "/Comandos/Solicitar", new() { ["tipo"] = CommandTypes.Run });
        await WebHarness.PostFormAsync(client, "/Comandos", "/Comandos/Solicitar", new() { ["tipo"] = CommandTypes.DryRun });

        (await db.QueryAsync<string>("SELECT tipo FROM solidesdp.comando")).Should().Equal(CommandTypes.DryRun);
    }

    [Fact]
    public async Task Admin_saves_a_new_configuration_version_and_invalid_rules_are_refused()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));
        var campos = new Dictionary<string, string>
        {
            ["Form.TiposColaborador"] = "1, x",
            ["Form.MaxCreatesPerRun"] = "50",
            ["Form.Observacao"] = "trava menor na carga inicial",
        };

        var invalida = await WebHarness.PostFormAsync(client, "/Configuracao", "/Configuracao/Salvar", new(campos));
        campos["Form.TiposColaborador"] = "1, 2, 3";
        var valida = await WebHarness.PostFormAsync(client, "/Configuracao", "/Configuracao/Salvar", new(campos));

        invalida.StatusCode.Should().Be(HttpStatusCode.OK, "\"x\" não é um tipo de colaborador");
        WebUtility.HtmlDecode(await invalida.Content.ReadAsStringAsync(Ct)).Should().Contain("\"x\" não é um número");
        valida.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var atual = await _store.GetCurrentConfigurationAsync("default", Ct);
        atual!.Version.Should().Be(1);
        var regras = SyncOptionsJson.Deserialize(atual.SyncJson);
        regras.TiposColaborador.Should().Equal(1, 2, 3);
        regras.MaxCreatesPerRun.Should().Be(50);
        regras.DryRun.Should().BeTrue("a simulação é decidida por empresa; a regra geral fica sempre em simulação");
    }

    [Fact]
    public async Task Consulta_profile_cannot_change_the_configuration()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var response = await WebHarness.PostFormAsync(client, "/Configuracao", "/Configuracao/Desativar",
            new() { ["motivo"] = "tentativa" });

        response.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.configuracao")).Should().Equal(0);
    }

    private Task<IReadOnlyList<string>> Auditoria(string login) =>
        db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login });
}
