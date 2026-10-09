using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlManagementStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly SqlStateStore _state = new(db.Connections, TimeProvider.System);
    private readonly SqlManagementStore _store = new(db.Connections, TimeProvider.System);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _state.EnsureSchemaAsync(Ct);
        await _store.EnsureSchemaAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Schema_creation_is_idempotent_and_extends_runs()
    {
        await _store.EnsureSchemaAsync(Ct);

        var tables = await db.QueryAsync<string>("SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('solidesdp')");
        tables.Should().Contain(new[] { "configuracao", "comando", "auditoria" });
        var columns = await db.QueryAsync<string>(
            "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('solidesdp.runs') AND name IN ('solicitado_por', 'config_versao')");
        columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Seed_writes_version_1_only_once()
    {
        await _store.SeedConfigurationAsync("adn", "{}", Ct);
        await _store.SeedConfigurationAsync("adn", """{ "DryRun": false }""", Ct);

        var current = await _store.GetCurrentConfigurationAsync("adn", Ct);
        current!.Version.Should().Be(1);
        current.Active.Should().BeTrue();
        current.SyncJson.Should().Be("{}");
        current.CreatedBy.Should().Be(ManagementUsers.System);
    }

    [Fact]
    public async Task Each_change_is_a_new_version_and_old_versions_are_kept()
    {
        await _store.SeedConfigurationAsync("adn", "{}", Ct);
        var second = await _store.AddConfigurationVersionAsync("adn", active: false, "{}", "pausa para conferência", "carlos", Ct);
        var other = await _store.AddConfigurationVersionAsync("outra", active: true, "{}", null, "carlos", Ct);

        second.Should().Be(2);
        other.Should().Be(1, "a numeração é por instância");
        var current = await _store.GetCurrentConfigurationAsync("adn", Ct);
        current!.Version.Should().Be(2);
        current.Active.Should().BeFalse();
        current.Note.Should().Be("pausa para conferência");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.configuracao WHERE instance_name = 'adn'")).Should().Equal(2);
    }

    [Fact]
    public async Task Companies_belong_to_a_version_and_are_carried_to_the_next_one()
    {
        await _store.SeedConfigurationAsync("adn", "{}", Ct);
        EmpresaConfiguracao[] empresas =
        [
            new() { Cdempresa = 15, Habilitada = true, DryRun = false, GoLiveDate = new DateOnly(2026, 11, 1), ModoEmpresa = ModosEmpresa.Nenhuma, Filiais = [10, 12] },
            new() { Cdempresa = 1, Habilitada = false },
        ];
        await _store.AddConfigurationVersionAsync("adn", active: true, "{}", empresas, "empresas", "carlos", Ct);
        await _store.AddConfigurationVersionAsync("adn", active: false, "{}", "pausa", "carlos", Ct);

        var current = await _store.GetCurrentConfigurationAsync("adn", Ct);

        current!.Version.Should().Be(3);
        current.Empresas.Should().BeEquivalentTo(empresas);
    }

    [Fact]
    public async Task The_latest_token_of_each_company_wins_and_null_removes_it()
    {
        await _store.AddEmpresaTokenAsync(15, [1, 2, 3], "carlos", Ct);
        await _store.AddEmpresaTokenAsync(15, [4, 5], "carlos", Ct);
        await _store.AddEmpresaTokenAsync(1, [9], "carlos", Ct);
        await _store.AddEmpresaTokenAsync(1, null, "carlos", Ct);

        var tokens = await _store.GetEmpresaTokensAsync(Ct);

        tokens[15].TokenCifrado.Should().Equal(4, 5);
        tokens[1].TokenCifrado.Should().BeNull();
    }

    [Fact]
    public async Task Commands_can_target_one_company()
    {
        await _store.EnqueueCommandAsync("adn", CommandTypes.DryRun, "carlos", 15, Ct);

        var claimed = await _store.ClaimNextCommandAsync("adn", Ct);

        claimed!.Cdempresa.Should().Be(15);
    }

    [Fact]
    public async Task Commands_are_claimed_in_order_once_and_completed()
    {
        var first = await _store.EnqueueCommandAsync("adn", CommandTypes.DryRun, "carlos", Ct);
        var second = await _store.EnqueueCommandAsync("adn", CommandTypes.Run, "ana", Ct);
        await _store.EnqueueCommandAsync("outra", CommandTypes.Run, "ana", Ct);

        var claimed = await _store.ClaimNextCommandAsync("adn", Ct);
        claimed.Should().BeEquivalentTo(new ClaimedCommand { Id = first, Type = CommandTypes.DryRun, RequestedBy = "carlos" });
        (await _store.ClaimNextCommandAsync("adn", Ct))!.Id.Should().Be(second);
        (await _store.ClaimNextCommandAsync("adn", Ct)).Should().BeNull("o comando da outra instância não é desta");

        await _store.CompleteCommandAsync(first, CommandStatuses.Done, null, "ok", Ct);
        var status = await db.QueryAsync<string>("SELECT status FROM solidesdp.comando WHERE id = @first", new { first });
        status.Should().Equal(CommandStatuses.Done);
    }

    [Fact]
    public async Task Unknown_command_type_is_refused()
    {
        var enqueue = () => _store.EnqueueCommandAsync("adn", "APAGAR_TUDO", "carlos", Ct);

        await enqueue.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Interrupted_commands_and_runs_are_marked_as_failed()
    {
        await _store.EnqueueCommandAsync("adn", CommandTypes.Run, "carlos", Ct);
        await _store.ClaimNextCommandAsync("adn", Ct);
        var runId = await _state.StartRunAsync("adn", 1, dryRun: false, trigger: "web", Ct);

        (await _store.FailInterruptedCommandsAsync("adn", Ct)).Should().Be(1);
        (await _state.FailInterruptedRunsAsync("adn", 1, Ct)).Should().Be(1);

        (await db.QueryAsync<string>("SELECT status FROM solidesdp.comando")).Should().Equal(CommandStatuses.Failed);
        (await db.QueryAsync<string>("SELECT status FROM solidesdp.runs WHERE run_id = @runId", new { runId })).Should().Equal(RunStatuses.Failed);
    }

    [Fact]
    public async Task Run_is_tagged_with_user_and_configuration_version()
    {
        var runId = await _state.StartRunAsync("adn", 1, dryRun: true, trigger: "web", Ct);

        await _store.TagRunAsync(runId, "carlos", 3, Ct);

        var tag = await db.QueryAsync<(string, int)>(
            "SELECT solicitado_por, config_versao FROM solidesdp.runs WHERE run_id = @runId", new { runId });
        tag.Should().Equal(("carlos", 3));
    }
}
