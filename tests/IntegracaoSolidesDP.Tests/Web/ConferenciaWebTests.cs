using System.Net;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Tela Conferência: lista, detalhe sem o CPF completo, aviso no Painel e "Reenviar do RHSenso".</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ConferenciaWebTests(SqlServerFixture db) : IAsyncLifetime
{
    private WebHarness _web = null!;
    private SqlStateStore _state = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _state = new SqlStateStore(db.Connections, TimeProvider.System);
        await _state.EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Operator_sees_the_differences_and_asks_to_resend_from_rhsenso()
    {
        var id = await SemearAsync();
        var login = await _web.CriarUsuarioAsync(Perfis.Operador);
        var client = await _web.EntrarAsync(login);

        (await client.GetStringAsync("/Conferencia", Ct)).Should().Contain($"data-conferencia=\"{id}\"");
        (await client.GetStringAsync("/", Ct)).Should().Contain("data-conferencia-divergente=\"15\"");
        var linhas = await client.GetStringAsync($"/Conferencia/Linhas/{id}", Ct);
        linhas.Should().Contain("MARIA SOUZA").And.NotContain(TestData.Cpf1, "a grade mostra só o CPF mascarado");

        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Reenviar/{id}",
            new() { ["rotulos"] = "15-00000001" });

        resposta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await _state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().BeNull("a próxima execução reenvia");
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().Contain(AcoesAuditoria.ReenvioSolicitado);
    }

    [Fact]
    public async Task Consulta_profile_sees_but_cannot_resend()
    {
        var id = await SemearAsync();
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var detalhe = await client.GetAsync($"/Conferencia/Detalhe/{id}", Ct);
        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Reenviar/{id}", new() { ["rotulos"] = "15-00000001" });

        detalhe.StatusCode.Should().Be(HttpStatusCode.OK);
        resposta.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await _state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().NotBeNull();
    }

    private async Task<long> SemearAsync()
    {
        await _state.UpsertEntityStateAsync(15, new EntityState
        {
            EntityType = EntityTypes.Employee, ExternalId = TestData.Cpf1, RemoteId = 9, PayloadHash = new string('a', 64),
            Status = EntityStatuses.Synced, Matricula = "00000001", Cdfilial = 1,
        }, Ct);
        var agora = DateTimeOffset.UtcNow;
        return await new SqlConferenciaStore(db.Connections).SalvarAsync(new Conferencia
        {
            Instancia = "default", Cdempresa = 15, IniciadaEm = agora, ConcluidaEm = agora, Gatilho = GatilhosConferencia.Agendada,
            Conferidos = 1, Pessoas = 1, Divergencias = 1,
            Linhas =
            [
                new DivergenciaCampo
                {
                    Rotulo = "15-00000001", Nome = "MARIA SOUZA", Cpf = TestData.Cpf1, CpfMascarado = "123.***.***-09", CadastroDp = 9,
                    Campo = CamposConferencia.Nome, RhSenso = "MARIA SOUZA", Dp = "MARIA",
                },
            ],
        }, Ct);
    }
}
