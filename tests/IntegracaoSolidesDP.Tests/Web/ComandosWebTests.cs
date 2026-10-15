using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Pedido em andamento: a tela acompanha a situação e, quando termina, mostra o resultado.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ComandosWebTests(SqlServerFixture db) : IAsyncLifetime
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
    public async Task An_open_request_shows_the_progress_and_its_situation_until_the_service_finishes()
    {
        var store = new SqlManagementStore(db.Connections, TimeProvider.System);
        var id = await store.EnqueueCommandAsync("default", CommandTypes.Run, "carlos", Ct);
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var detalhe = await client.GetStringAsync($"/Comandos/Detalhe/{id}", Ct);
        var emAberto = await client.GetStringAsync($"/Comandos/Situacao/{id}", Ct);
        await store.CompleteCommandAsync(id, CommandStatuses.Done, runId: null, "ok", Ct);
        var concluido = await client.GetStringAsync($"/Comandos/Situacao/{id}", Ct);

        detalhe.Should().Contain("id=\"processando\"").And.Contain($"/Comandos/Situacao/{id}");
        emAberto.Should().Contain("\"emAberto\":true").And.Contain($"\"status\":\"{CommandStatuses.Pending}\"");
        concluido.Should().Contain("\"emAberto\":false");
        (await client.GetStringAsync($"/Comandos/Detalhe/{id}", Ct)).Should().NotContain("id=\"processando\"");
    }
}
