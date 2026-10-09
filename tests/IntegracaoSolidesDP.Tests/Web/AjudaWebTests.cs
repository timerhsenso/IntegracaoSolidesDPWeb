using System.Net;
using System.Reflection;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

[Collection(SqlServerCollection.Name)]
public sealed class AjudaWebTests(SqlServerFixture db) : IAsyncLifetime
{
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
    public async Task Help_page_opens_for_the_lowest_profile_with_every_topic_but_without_the_technical_manual()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var response = await client.GetAsync("/Ajuda", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AjudaTopicos.Todos.Should().AllSatisfy(t => html.Should().Contain($"id=\"{t.Id}\""));
        html.Should().NotContain("/Ajuda/ManualTecnico");
    }

    [Fact]
    public async Task Help_documents_every_configuration_field_and_every_audit_action()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Ajuda", Ct));

        CamposConfiguracao.Todos.Should().AllSatisfy(c => html.Should().Contain($"id=\"{c.Ancora}\""));
        var acoes = typeof(AcoesAuditoria).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!);
        acoes.Should().AllSatisfy(a => html.Should().Contain($"<code>{a}</code>", "toda ação gravada na auditoria precisa estar explicada"));
    }

    [Fact]
    public async Task Configuration_screen_shows_the_hint_and_the_manual_link_of_every_field()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Configuracao", Ct));

        CamposConfiguracao.Todos.Should().AllSatisfy(c =>
        {
            html.Should().Contain($"/Ajuda#{c.Ancora}", $"o campo {c.Propriedade} precisa do <dica-campo>");
            html.Should().Contain(c.Dica);
        });
    }

    [Fact]
    public async Task Every_screen_with_a_topic_opens_its_contextual_help()
    {
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));

        foreach (var url in new[] { "/", "/Execucoes", "/Migrados", "/Pendencias", "/Comandos", "/Configuracao", "/Usuarios", "/Auditoria", "/Conta/AlterarSenha" })
        {
            var html = await client.GetStringAsync(url, Ct);
            html.Should().Contain("id=\"ajuda-tela\"", $"a tela {url} deveria ter o botão Ajuda");
        }
    }

    [Fact]
    public async Task Technical_manual_is_a_pdf_downloadable_only_by_the_admin()
    {
        var operador = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Operador));
        var admin = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));

        var negado = await operador.GetAsync("/Ajuda/ManualTecnico", Ct);
        var pdf = await admin.GetAsync("/Ajuda/ManualTecnico", Ct);
        var bytes = await pdf.Content.ReadAsByteArrayAsync(Ct);

        negado.StatusCode.Should().Be(HttpStatusCode.Redirect);
        negado.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }
}
