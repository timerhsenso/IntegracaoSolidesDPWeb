using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlStateStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly SqlStateStore _store = new(db.Connections, TimeProvider.System);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _store.EnsureSchemaAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Schema_creation_is_idempotent_and_lives_in_its_own_schema()
    {
        await _store.EnsureSchemaAsync(Ct);

        var tables = await db.QueryAsync<string>("SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('solidesdp')");
        tables.Should().BeEquivalentTo([
            "meta", "runs", "run_items", "colaborador_vinculo", "cargo_vinculo", "local_vinculo", "ferias_vinculo",
            "entity_state", "vacation_state",
        ]);
    }

    [Fact]
    public async Task Tables_and_columns_are_documented_in_extended_properties()
    {
        await _store.EnsureSchemaAsync(Ct);

        var descricao = await db.QueryAsync<string>("""
            SELECT CAST(ep.value AS nvarchar(4000))
            FROM sys.extended_properties ep
            WHERE ep.major_id = OBJECT_ID(N'solidesdp.colaborador_vinculo') AND ep.name = N'MS_Description'
              AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'solidesdp.colaborador_vinculo'), N'cpf', 'ColumnId')
            """);
        descricao.Should().ContainSingle().Which.Should().Contain("CPF");
    }

    [Fact]
    public async Task Run_lock_is_exclusive_per_instance_and_company_and_released_on_dispose()
    {
        var first = await _store.TryAcquireRunLockAsync("adn", 15, Ct);
        var second = await _store.TryAcquireRunLockAsync("adn", 15, Ct);
        var otherCompany = await _store.TryAcquireRunLockAsync("adn", 1, Ct);
        var otherInstance = await _store.TryAcquireRunLockAsync("outra", 15, Ct);

        first.Should().NotBeNull();
        second.Should().BeNull();
        otherCompany.Should().NotBeNull();
        otherInstance.Should().NotBeNull();

        await first!.DisposeAsync();
        await otherCompany!.DisposeAsync();
        await otherInstance!.DisposeAsync();
        var again = await _store.TryAcquireRunLockAsync("adn", 15, Ct);
        again.Should().NotBeNull();
        await again!.DisposeAsync();
    }

    [Fact]
    public async Task Key_scheme_is_recorded_once_and_a_different_one_is_refused()
    {
        await _store.VerifyKeySchemeAsync("cpf-por-empresa-v2", Ct);
        await _store.VerifyKeySchemeAsync("cpf-por-empresa-v2", Ct);

        var change = () => _store.VerifyKeySchemeAsync("empresa-matricula-v1", Ct);
        await change.Should().ThrowAsync<InvalidOperationException>().WithMessage("*duplicaria colaboradores*");
    }

    [Fact]
    public async Task Employee_links_are_per_company_and_upsert_by_cpf()
    {
        var link = new EntityState
        {
            EntityType = EntityTypes.Employee, ExternalId = TestData.Cpf1, RemoteId = 10, PayloadHash = new string('a', 64),
            Status = EntityStatuses.Synced, ExtraJson = "{}", CodigoExterno = "00007811", Matricula = "00007811", Cdfilial = 1,
            Origem = OrigensVinculo.VinculadoCpf,
        };
        await _store.UpsertEntityStateAsync(15, link, Ct);
        await _store.UpsertEntityStateAsync(15, link with { Status = EntityStatuses.Dismissed }, Ct);
        await _store.UpsertEntityStateAsync(1, link with { RemoteId = 99, Origem = OrigensVinculo.Criado }, Ct);

        var empresa15 = await _store.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct);
        var empresa1 = await _store.LoadEntityStatesAsync(1, EntityTypes.Employee, Ct);

        empresa15.Should().ContainSingle();
        empresa15[TestData.Cpf1].Should().BeEquivalentTo(link with { Status = EntityStatuses.Dismissed }, o => o.Excluding(s => s.UpdatedAt));
        empresa1[TestData.Cpf1].RemoteId.Should().Be(99);
    }

    [Fact]
    public async Task Job_role_and_workplace_links_round_trip()
    {
        await _store.UpsertEntityStateAsync(15, new EntityState
        {
            EntityType = EntityTypes.JobRole, ExternalId = "00100", RemoteId = 7, PayloadHash = new string('c', 64), Status = EntityStatuses.Synced,
        }, Ct);
        await _store.UpsertEntityStateAsync(15, new EntityState
        {
            EntityType = EntityTypes.Workplace, ExternalId = "15-3", Cdfilial = 3, RemoteId = 8, PayloadHash = new string('d', 64), Status = EntityStatuses.Synced,
        }, Ct);

        (await _store.LoadEntityStatesAsync(15, EntityTypes.JobRole, Ct))["00100"].RemoteId.Should().Be(7);
        (await _store.LoadEntityStatesAsync(15, EntityTypes.Workplace, Ct))["15-3"].RemoteId.Should().Be(8);
        (await _store.LoadEntityStatesAsync(1, EntityTypes.Workplace, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Vacation_links_round_trip_per_company()
    {
        var state = new VacationState
        {
            Feria2Id = Guid.NewGuid(), EmployeeExternalId = TestData.Cpf1, RemoteAdjustmentId = 5, PayloadHash = new string('b', 64),
            Status = VacationStatuses.Pending, Attempts = 2, LastError = "x", StartDate = 1, EndDate = 2,
        };
        await _store.UpsertVacationStateAsync(15, state, Ct);

        (await _store.LoadVacationStatesAsync(15, Ct))[state.Feria2Id].Should().BeEquivalentTo(state, o => o.Excluding(s => s.UpdatedAt));
        (await _store.LoadVacationStatesAsync(1, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Runs_and_items_are_recorded_with_the_company()
    {
        var runId = await _store.StartRunAsync("adn", 15, dryRun: true, trigger: "test", Ct);
        await _store.AddItemsAsync(runId, [new RunItem("employee", "15-1", "create", "dry_run", null, "ok")], Ct);
        await _store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);

        var run = await db.QueryAsync<(string, int)>("SELECT status, cdempresa FROM solidesdp.runs WHERE run_id = @runId", new { runId });
        var items = await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @runId", new { runId });
        run.Should().Equal((RunStatuses.Completed, 15));
        items.Should().Equal(1);
    }

    [Fact]
    public async Task Interrupted_runs_are_failed_only_for_the_company()
    {
        var mine = await _store.StartRunAsync("adn", 15, dryRun: true, trigger: "test", Ct);
        var other = await _store.StartRunAsync("adn", 1, dryRun: true, trigger: "test", Ct);

        (await _store.FailInterruptedRunsAsync("adn", 15, Ct)).Should().Be(1);

        (await db.QueryAsync<string>("SELECT status FROM solidesdp.runs WHERE run_id = @mine", new { mine })).Should().Equal(RunStatuses.Failed);
        (await db.QueryAsync<string>("SELECT status FROM solidesdp.runs WHERE run_id = @other", new { other })).Should().Equal(RunStatuses.Running);
    }
}
