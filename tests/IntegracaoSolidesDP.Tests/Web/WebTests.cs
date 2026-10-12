using System.Net;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Infrastructure;
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
    public async Task Dashboard_shows_the_service_signal_and_warns_when_no_service_answers()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));
        var sinais = new SqlSinalServicoStore(db.Connections);
        var agora = DateTimeOffset.UtcNow;
        await sinais.RegistrarAsync(new SinalServico
        {
            Instancia = "default", Maquina = "SRV-TESTE", Versao = "9.9.9", IniciadoEm = agora.AddHours(-2), UltimoSinal = agora.AddMinutes(-30),
        }, Ct);

        var parado = await client.GetStringAsync("/", Ct);
        var pedidosParado = await client.GetStringAsync("/Comandos", Ct);

        parado.Should().Contain("data-situacao-servico=\"parado\"").And.Contain("SRV-TESTE").And.Contain("9.9.9");
        pedidosParado.Should().Contain("data-situacao-servico=\"parado\"", "a tela de pedidos também avisa");

        await sinais.RegistrarAsync(new SinalServico
        {
            Instancia = "default", Maquina = "SRV-TESTE", Versao = "9.9.9", IniciadoEm = agora.AddHours(-2), UltimoSinal = DateTimeOffset.UtcNow,
        }, Ct);

        (await client.GetStringAsync("/", Ct)).Should().NotContain("data-situacao-servico=\"parado\"").And.Contain("SRV-TESTE");
        (await client.GetStringAsync("/Comandos", Ct)).Should().NotContain("data-situacao-servico");
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

    [Fact]
    public async Task Simulation_offers_the_linking_report_for_download_and_the_download_is_audited()
    {
        var store = new SqlStateStore(db.Connections, TimeProvider.System);
        var runId = await store.StartRunAsync("default", 15, dryRun: true, "web", Ct);
        await store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);
        await store.SaveRunReportAsync(runId, "vinculacao", "vinculacao-empresa15-20261011-212019.csv", "Matrícula;Nome no RHSenso\n15-00000930;CLÁUDIO\n", Ct);
        var login = await _web.CriarUsuarioAsync(Perfis.Consulta);
        var client = await _web.EntrarAsync(login);

        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Execucoes/Detalhe/{runId}", Ct));
        var resposta = await client.GetAsync($"/Execucoes/Relatorio/{runId}?tipo=vinculacao", Ct);

        detalhe.Should().Contain($"/Execucoes/Relatorio/{runId}?tipo=vinculacao");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        resposta.Content.Headers.ContentDisposition!.ToString().Should().Contain("vinculacao-empresa15-20261011-212019.csv");
        var bytes = await resposta.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "BOM para o Excel abrir a acentuação");
        System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Contain("CLÁUDIO");
        (await Auditoria(login)).Should().Contain(AcoesAuditoria.RelatorioBaixado);
        detalhe.Should().NotContain("vinculacao-grade", "simulação antiga, só com o CSV: a tela oferece o download");
    }

    [Fact]
    public async Task Simulation_shows_the_linking_report_on_the_screen()
    {
        var store = new SqlStateStore(db.Connections, TimeProvider.System);
        var runId = await store.StartRunAsync("default", 15, dryRun: true, "web", Ct);
        await store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);
        await store.SaveRunReportAsync(runId, "vinculacao", "vinculacao-empresa15-20261012-093828.csv", "Matrícula;Nome no RHSenso\n15-00000930;CLÁUDIO\n", Ct);
        await store.SaveRunReportAsync(runId, "vinculacao-linhas", "vinculacao-empresa15-20261012-093828.json",
            """[{"matricula":"15-00000930","nome":"CLÁUDIO","resultado":"Vincular pelo CPF"}]""", Ct);
        var login = await _web.CriarUsuarioAsync(Perfis.Consulta);
        var client = await _web.EntrarAsync(login);

        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Execucoes/Detalhe/{runId}", Ct));
        var resposta = await client.GetAsync($"/Execucoes/Vinculacao/{runId}", Ct);

        detalhe.Should().Contain("id=\"vinculacao-grade\"").And.Contain($"/Execucoes/Vinculacao/{runId}");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await resposta.Content.ReadAsStringAsync(Ct)).Should().Contain("CLÁUDIO").And.Contain("Vincular pelo CPF");
        (await Auditoria(login)).Should().NotContain(AcoesAuditoria.RelatorioBaixado, "ver na tela não é baixar");
    }

    [Fact]
    public async Task Menu_and_run_detail_show_the_installed_versions()
    {
        var store = new SqlStateStore(db.Connections, TimeProvider.System);
        var runId = await store.StartRunAsync("default", 15, dryRun: true, "web", Ct);
        await store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Execucoes/Detalhe/{runId}", Ct));

        detalhe.Should().Contain($"Serviço {VersaoServico.Atual}", "rodapé do menu")
            .And.Contain($"Versão do serviço</dt><dd class=\"col-7\">{VersaoServico.Atual}</dd>", "detalhe da execução");
    }

    [Fact]
    public async Task Run_without_report_has_no_download()
    {
        var store = new SqlStateStore(db.Connections, TimeProvider.System);
        var runId = await store.StartRunAsync("default", 15, dryRun: false, "web", Ct);
        await store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Execucoes/Detalhe/{runId}", Ct));
        var resposta = await client.GetAsync($"/Execucoes/Relatorio/{runId}?tipo=vinculacao", Ct);

        // O texto do botão também aparece na Ajuda da tela: confere o link do download.
        detalhe.Should().NotContain("/Execucoes/Relatorio/").And.NotContain("vinculacao-grade");
        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/Execucoes/Vinculacao/{runId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<IReadOnlyList<string>> Auditoria(string login) =>
        db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login });
}
