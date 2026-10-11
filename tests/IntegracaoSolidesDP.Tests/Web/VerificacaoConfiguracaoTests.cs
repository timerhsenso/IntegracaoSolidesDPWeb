using System.Net;
using System.Text.RegularExpressions;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>
/// <c>IntegracaoSolidesDP.Web.exe --check-config</c> (usado pelo install-web.ps1) e o
/// deploy/windows/permissoes-sql.sql: a Web sobe e funciona com o login de privilégio mínimo.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed partial class VerificacaoConfiguracaoTests(SqlServerFixture db) : IAsyncLifetime
{
    private const string SenhaServico = "Servico!Forte123";
    private const string SenhaWeb = "Web!Forte12345";

    private static readonly string ScriptPermissoes =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sql", "permissoes-sql.sql"));

    private readonly SqlManagementStore _store = new(db.Connections, TimeProvider.System);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConexaoWeb => new SqlConnectionStringBuilder(db.ConnectionString)
    {
        UserID = "integracao_solidesdp_web",
        Password = SenhaWeb,
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await _store.EnsureSchemaAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Check_config_passes_with_the_service_tables_and_the_key()
    {
        await using var web = new WebHarness(db);

        var (ok, saida) = await VerificarAsync(web);

        ok.Should().BeTrue(saida);
        saida.Should().Contain("[OK] Banco: bd_rhu_adn")
            .And.Contain("[OK] Tabelas do login (solidesdp_auth) em dia")
            .And.Contain("[OK] Tabelas da gestão criadas pelo serviço")
            .And.NotContain("[ERRO]");
    }

    [Fact]
    public async Task Token_written_with_another_key_is_a_warning_and_is_never_shown()
    {
        var outraChave = new TokenProtector(new TokenProtectionOptions { ChaveBase64 = TokenProtector.NovaChave() });
        await _store.AddEmpresaTokenAsync(15, outraChave.Protect("token-secreto"), "teste", Ct);
        await using var web = new WebHarness(db);

        var (ok, saida) = await VerificarAsync(web);

        // A Web não decifra tokens: sobe normalmente, e é nela que o token é cadastrado de novo.
        ok.Should().BeTrue(saida);
        saida.Should().Contain("[AVISO] Empresa 15").And.NotContain("token-secreto");
    }

    [Fact]
    public async Task Web_login_created_by_the_permissions_script_starts_the_web_and_passes_the_check()
    {
        await RodarScriptPermissoesAsync();
        await using var web = new WebHarness(db, ConexaoWeb);

        var (ok, saida) = await VerificarAsync(web);

        ok.Should().BeTrue(saida);
        saida.Should().Contain("login integracao_solidesdp_web").And.NotContain("[ERRO]");

        // E a Web funciona de verdade com esse login: cria usuário, entra (audita) e abre o painel.
        var login = await web.CriarUsuarioAsync(Perfis.Admin);
        var client = await web.EntrarAsync(login);
        (await client.GetAsync("/", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Web_login_without_write_permission_on_solidesdp_is_an_error()
    {
        await RodarScriptPermissoesAsync();
        await db.ExecuteAsync("REVOKE INSERT, UPDATE ON SCHEMA::solidesdp FROM integracao_solidesdp_web");
        try
        {
            await using var web = new WebHarness(db, ConexaoWeb);

            var (ok, saida) = await VerificarAsync(web);

            ok.Should().BeFalse();
            saida.Should().Contain("[ERRO] Falta permissão de INSERT, UPDATE no schema solidesdp");
        }
        finally
        {
            await db.ExecuteAsync("GRANT INSERT, UPDATE ON SCHEMA::solidesdp TO integracao_solidesdp_web");
        }
    }

    [Fact]
    public async Task Permissions_script_refuses_to_run_with_the_placeholder_passwords()
    {
        var act = () => RodarScriptAsync(ScriptPermissoes);

        await act.Should().ThrowAsync<SqlException>().WithMessage("*Troque as senhas*");
    }

    private async Task<(bool Ok, string Saida)> VerificarAsync(WebHarness web)
    {
        await using var saida = new StringWriter();
        var ok = await VerificacaoConfiguracao.ExecutarAsync(web.Services, saida, Ct);
        return (ok, saida.ToString());
    }

    private Task RodarScriptPermissoesAsync() => RodarScriptAsync(ScriptPermissoes
        .Replace("<senha forte do serviço>", SenhaServico, StringComparison.Ordinal)
        .Replace("<senha forte da Web>", SenhaWeb, StringComparison.Ordinal));

    /// <summary>Como o SSMS/sqlcmd: lotes separados por GO, todos na mesma conexão (USE e NOEXEC valem para os seguintes).</summary>
    private async Task RodarScriptAsync(string script)
    {
        // Sem pool: o SET NOEXEC ON do script (senhas não trocadas) não pode sobrar numa conexão reaproveitada.
        var semPool = new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(semPool);
        await connection.OpenAsync(Ct);
        foreach (var lote in SeparadorGo().Split(script).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            await using var command = new SqlCommand(lote, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SeparadorGo();
}
