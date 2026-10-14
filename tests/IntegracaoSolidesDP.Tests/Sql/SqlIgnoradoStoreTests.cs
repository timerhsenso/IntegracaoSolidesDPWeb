using IntegracaoSolidesDP.Worker.Management;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlIgnoradoStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private SqlIgnoradoStore _store = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _store = new SqlIgnoradoStore(db.Connections);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Without_the_table_nobody_is_ignored()
    {
        (await _store.VigentesAsync(15, Ct)).Should().BeEmpty("sem a gestão a tabela não existe e a simulação segue normal");
        (await _store.HistoricoAsync(15, 10, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ignored_people_are_kept_per_company_without_duplicates_and_return_with_who_and_why()
    {
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);

        var incluidos = await _store.IgnorarAsync(15,
            [new NovoIgnorado(TestData.Cpf3, 5538323, "CLEBER"), new NovoIgnorado(null, 2652341, "SEM CPF")], "carlos", Ct);
        var repetidos = await _store.IgnorarAsync(15, [new NovoIgnorado(TestData.Cpf3, 999, "CLEBER (outro cadastro)")], "ana", Ct);
        await _store.IgnorarAsync(14, [new NovoIgnorado(TestData.Cpf3, 1, "CLEBER NA 14")], "carlos", Ct);

        incluidos.Should().Be(2);
        repetidos.Should().Be(0, "o mesmo CPF já está ignorado na empresa");
        var vigentes = await _store.VigentesAsync(15, Ct);
        vigentes.Should().HaveCount(2);
        var cleber = vigentes.Single(i => i.Cpf == TestData.Cpf3);
        cleber.Should().Match<ColaboradorIgnorado>(i => i.CadastroDp == 5538323 && i.NomeDp == "CLEBER" && i.IgnoradoPor == "carlos" && i.Vigente);
        vigentes.Single(i => i.Cpf == null).Corresponde(2652341, null).Should().BeTrue();

        (await _store.RemoverAsync(15, cleber.Id, SqlIgnoradoStore.Servico, "apareceu ativo na folha", Ct)).Should().BeTrue();
        (await _store.RemoverAsync(15, cleber.Id, "carlos", "de novo", Ct)).Should().BeFalse("já não estava ignorado");

        (await _store.VigentesAsync(15, Ct)).Should().ContainSingle().Which.NomeDp.Should().Be("SEM CPF");
        (await _store.VigentesAsync(14, Ct)).Should().ContainSingle();
        var historico = await _store.HistoricoAsync(15, 10, Ct);
        historico.Should().HaveCount(2);
        historico[0].Vigente.Should().BeTrue("os ignorados em vigor vêm primeiro");
        historico[1].Should().Match<ColaboradorIgnorado>(i =>
            i.RemovidoPor == SqlIgnoradoStore.Servico && i.MotivoRemocao == "apareceu ativo na folha" && i.RemovidoEm != null);
    }
}
