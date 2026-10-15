using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Sql;

/// <summary>A Web e o serviço criam as tabelas ao mesmo tempo na partida.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlEsquemaTests(SqlServerFixture db) : IAsyncLifetime
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await db.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Creating_the_schema_at_the_same_time_from_several_places_does_not_fail()
    {
        var estado = new SqlStateStore(db.Connections, TimeProvider.System);
        var gestao = new SqlManagementStore(db.Connections, TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => i % 2 == 0 ? estado.EnsureSchemaAsync(Ct) : gestao.EnsureSchemaAsync(Ct)));

        (await db.QueryAsync<int>("""
            SELECT COUNT(*) FROM sys.extended_properties
            WHERE major_id = OBJECT_ID(N'solidesdp.erro_config') AND name = N'MS_Description'
            """)).Should().ContainSingle().Which.Should().Be(6, "a tabela e as cinco colunas documentadas, uma vez só");
    }
}
