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
        tables.Should().BeEquivalentTo(["meta", "runs", "run_items", "entity_state", "vacation_state"]);
    }

    [Fact]
    public async Task Run_lock_is_exclusive_per_instance_and_released_on_dispose()
    {
        var first = await _store.TryAcquireRunLockAsync("adn", Ct);
        var second = await _store.TryAcquireRunLockAsync("adn", Ct);
        var other = await _store.TryAcquireRunLockAsync("outra", Ct);

        first.Should().NotBeNull();
        second.Should().BeNull();
        other.Should().NotBeNull();

        await first!.DisposeAsync();
        await other!.DisposeAsync();
        var again = await _store.TryAcquireRunLockAsync("adn", Ct);
        again.Should().NotBeNull();
        await again!.DisposeAsync();
    }

    [Fact]
    public async Task Key_scheme_is_recorded_once_and_a_different_one_is_refused()
    {
        await _store.VerifyKeySchemeAsync("empresa-matricula-v1", Ct);
        await _store.VerifyKeySchemeAsync("empresa-matricula-v1", Ct);

        var change = () => _store.VerifyKeySchemeAsync("empresa-filial-matricula", Ct);
        await change.Should().ThrowAsync<InvalidOperationException>().WithMessage("*duplicaria colaboradores*");
    }

    [Fact]
    public async Task Entity_state_round_trips_and_upserts()
    {
        var state = new EntityState { EntityType = EntityTypes.Employee, ExternalId = "1-00000001", RemoteId = 10, PayloadHash = new string('a', 64), Status = EntityStatuses.Synced, ExtraJson = "{}" };
        await _store.UpsertEntityStateAsync(state, Ct);
        await _store.UpsertEntityStateAsync(state with { Status = EntityStatuses.Dismissed }, Ct);

        var loaded = await _store.LoadEntityStatesAsync(EntityTypes.Employee, Ct);

        loaded.Should().ContainSingle();
        loaded["1-00000001"].Should().BeEquivalentTo(state with { Status = EntityStatuses.Dismissed }, o => o.Excluding(s => s.UpdatedAt));
    }

    [Fact]
    public async Task Vacation_state_round_trips()
    {
        var state = new VacationState
        {
            Feria2Id = Guid.NewGuid(), EmployeeExternalId = "1-1", RemoteAdjustmentId = 5, PayloadHash = new string('b', 64),
            Status = VacationStatuses.Pending, Attempts = 2, LastError = "x", StartDate = 1, EndDate = 2,
        };
        await _store.UpsertVacationStateAsync(state, Ct);

        (await _store.LoadVacationStatesAsync(Ct))[state.Feria2Id].Should().BeEquivalentTo(state, o => o.Excluding(s => s.UpdatedAt));
    }

    [Fact]
    public async Task Runs_and_items_are_recorded()
    {
        var runId = await _store.StartRunAsync("adn", dryRun: true, "test", Ct);
        await _store.AddItemsAsync(runId, [new RunItem("employee", "1-1", "create", "dry_run", null, "ok")], Ct);
        await _store.FinishRunAsync(runId, RunStatuses.Completed, "{}", null, Ct);

        var status = await db.QueryAsync<string>("SELECT status FROM solidesdp.runs WHERE run_id = @runId", new { runId });
        var items = await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @runId", new { runId });
        status.Should().Equal(RunStatuses.Completed);
        items.Should().Equal(1);
    }
}
