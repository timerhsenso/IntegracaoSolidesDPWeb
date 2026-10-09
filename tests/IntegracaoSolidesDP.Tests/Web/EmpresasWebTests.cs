using System.Net;
using IntegracaoSolidesDP.Tests.EndToEnd;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Tela Empresas: configuração por empresa (versão nova), token cifrado e nunca exibido, pedidos por empresa.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class EmpresasWebTests(SqlServerFixture db) : IAsyncLifetime
{
    private const string Pagina = "/Empresas/Editar/15";
    private readonly SqlManagementStore _store = new(db.Connections, TimeProvider.System);
    private readonly RhuSeed _seed = new(db);
    private WebHarness _web = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await _store.EnsureSchemaAsync(Ct);
        await _seed.FilialAsync(empresa: 15, filial: 1, nome: "ADN PROJETOS");
        await _seed.FilialAsync(empresa: 15, filial: 2, nome: "ADN CAMACARI", cnpj: "00594807000361");
        await _seed.FilialAsync(empresa: 15, filial: 3, nome: "FILIAL INATIVA", cnpj: "11222333000181", ativa: false);
        await _store.SeedConfigurationAsync("default",
            SyncOptionsJson.Serialize(new SyncOptions()), Ct);
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Lists_the_active_companies_of_the_payroll()
    {
        await _seed.EmpresaAsync(20, ativa: false, nome: "EMPRESA INATIVA");
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Empresas", Ct));

        html.Should().Contain("ADN SERVICOS").And.Contain("/Empresas/Editar/15").And.NotContain("EMPRESA INATIVA");
    }

    [Fact]
    public async Task Admin_saves_the_company_as_a_new_configuration_version_with_only_active_branches()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);

        var inativa = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Salvar", Campos(filial: 3));
        var valida = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Salvar", Campos(filial: 2));

        inativa.StatusCode.Should().Be(HttpStatusCode.OK, "a filial 3 está inativa no RHSenso");
        valida.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var atual = await _store.GetCurrentConfigurationAsync("default", Ct);
        atual!.Version.Should().Be(2);
        var empresa = atual.Empresas.Should().ContainSingle().Subject;
        empresa.Cdempresa.Should().Be(15);
        empresa.Habilitada.Should().BeTrue();
        empresa.DryRun.Should().BeTrue();
        empresa.Filiais.Should().Equal(2);
        empresa.Piloto.Should().Equal("15-00007811", "12345678901");
        empresa.ModoEmpresa.Should().Be(ModosEmpresa.PorCnpj);
        (await AcoesAsync(login)).Should().Contain(AcoesAuditoria.EmpresaAlterada);
    }

    [Fact]
    public async Task Real_sending_requires_the_company_go_live()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));
        var campos = Campos(filial: 2);
        campos["Form.DryRun"] = "false";

        var semGoLive = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Salvar", new(campos));
        campos["Form.GoLiveDate"] = "2026-11-01";
        var comGoLive = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Salvar", new(campos));

        semGoLive.StatusCode.Should().Be(HttpStatusCode.OK, "sem go-live a simulação não pode ser desligada");
        (await semGoLive.Content.ReadAsStringAsync(Ct)).Should().Contain("go-live");
        comGoLive.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var empresa = (await _store.GetCurrentConfigurationAsync("default", Ct))!.Empresas.Single();
        empresa.DryRun.Should().BeFalse();
        empresa.GoLiveDate.Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task Token_is_stored_encrypted_and_never_shown_or_audited()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);

        var response = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Token/15", new() { ["token"] = "Basic tok-SECRETO-123" });

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var stored = (await _store.GetEmpresaTokensAsync(Ct))[15];
        System.Text.Encoding.UTF8.GetString(stored.TokenCifrado!).Should().NotContain("SECRETO");
        new TokenProtector(new TokenProtectionOptions { ChaveBase64 = E2EHarness.ChaveTokens }).Unprotect(stored.TokenCifrado!)
            .Should().Be("tok-SECRETO-123", "o prefixo Basic é retirado");
        (await client.GetStringAsync(Pagina, Ct)).Should().NotContain("SECRETO");
        var auditoria = await db.QueryAsync<string>("SELECT CONCAT(acao, ' ', detalhe) FROM solidesdp.auditoria WHERE usuario = @login", new { login });
        auditoria.Should().Contain(a => a.StartsWith(AcoesAuditoria.EmpresaTokenCadastrado, StringComparison.Ordinal))
            .And.NotContain(a => a.Contains("SECRETO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Consulta_profile_cannot_store_a_token()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var response = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Token/15", new() { ["token"] = "abc" });

        response.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await _store.GetEmpresaTokensAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Operador_requests_a_dry_run_of_one_company()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Operador));

        var response = await WebHarness.PostFormAsync(client, Pagina, "/Empresas/Solicitar/15", new() { ["tipo"] = CommandTypes.DryRun });

        response.Headers.Location!.ToString().Should().Contain("/Comandos/Detalhe/");
        (await db.QueryAsync<(string, int)>("SELECT tipo, cdempresa FROM solidesdp.comando")).Should().Equal((CommandTypes.DryRun, 15));
    }

    private static Dictionary<string, string> Campos(int filial) => new()
    {
        ["Form.Cdempresa"] = "15",
        ["Form.Habilitada"] = "true",
        ["Form.DryRun"] = "true",
        ["Form.Filiais"] = filial.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Form.ModoEmpresa"] = ModosEmpresa.PorCnpj,
        ["Form.Piloto"] = "15-00007811\n 12345678901 ;",
        ["Form.Observacao"] = "piloto da empresa 15",
    };

    private Task<IReadOnlyList<string>> AcoesAsync(string login) =>
        db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login });
}
