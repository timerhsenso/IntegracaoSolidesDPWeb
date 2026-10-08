using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.EndToEnd;

/// <summary>Gestão pela Web (Gestao:Habilitada): configuração no banco, ativar/desativar e fila de comandos.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ManagementEndToEndTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly RhuSeed _seed = new(db);
    private readonly SqlManagementStore _store = new(db.Connections, TimeProvider.System);
    private E2EHarness _harness = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _seed.BasicsAsync();
        await _seed.FuncionarioAsync(matric: "00000001", cpf: TestData.Cpf1);
        _harness = new E2EHarness(db);
        _harness.Settings["Gestao:Habilitada"] = "true";
        await _harness.Fake.ResetAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task First_run_creates_version_1_from_appsettings_and_records_it()
    {
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        current!.Version.Should().Be(1);
        SyncOptionsJson.Deserialize(current.SyncJson).GoLiveDate.Should().Be(new DateOnly(2026, 1, 1));
        var version = await db.QueryAsync<int>("SELECT config_versao FROM solidesdp.runs WHERE run_id = @RunId", new { summary.RunId });
        version.Should().Equal(1);
    }

    [Fact]
    public async Task Disabled_integration_does_not_run_but_still_allows_a_dry_run()
    {
        await _harness.RunAsync(Ct);
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        await _store.AddConfigurationVersionAsync("default", active: false, current!.SyncJson, "pausa", "carlos", Ct);
        await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2, nome: "JOAO SANTOS");
        await _harness.Fake.ClearRequestsAsync(Ct);

        var real = await _harness.RunAsync(Ct);
        var dryRun = await RunCommandAsync(CommandTypes.DryRun);

        real.Status.Should().Be(RunStatuses.SkippedDisabled);
        dryRun.Status.Should().Be(CommandStatuses.Done, dryRun.Result);
        (await _harness.WritesAsync(Ct)).Should().BeEmpty("a integração está desativada e o dry-run não chama a API");
    }

    [Fact]
    public async Task Rules_come_from_the_database_not_from_appsettings()
    {
        await _harness.RunAsync(Ct);
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        var rules = SyncOptionsJson.Deserialize(current!.SyncJson);
        rules.DryRun = true;
        await _store.AddConfigurationVersionAsync("default", active: true, SyncOptionsJson.Serialize(rules), "volta ao dry-run", "carlos", Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.DryRun.Should().BeTrue("a versão 2 do banco liga o dry-run, embora o appsettings diga false");
    }

    [Fact]
    public async Task Invalid_configuration_is_recorded_as_a_failed_run_without_calling_the_api()
    {
        await EnsureSchemasAsync();
        await _store.AddConfigurationVersionAsync("default", active: true, """{ "DryRun": false, "GoLiveDate": null }""", null, "carlos", Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("config_invalid").And.Contain("GoLiveDate");
        (await _harness.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_command_executes_the_sync_on_behalf_of_the_user()
    {
        var command = await RunCommandAsync(CommandTypes.Run);

        command.Status.Should().Be(CommandStatuses.Done, command.Result);
        command.RunId.Should().NotBeNull();
        var run = await db.QueryAsync<(string, string)>(
            "SELECT triggered_by, solicitado_por FROM solidesdp.runs WHERE run_id = @RunId", new { command.RunId });
        run.Should().Equal(("web", "carlos"));
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle(e => e.ExternalId == "1-00000001");
    }

    [Fact]
    public async Task Check_config_command_stores_the_console_output()
    {
        var command = await RunCommandAsync(CommandTypes.CheckConfig);

        command.Status.Should().Be(CommandStatuses.Done, command.Result);
        command.Result.Should().Contain("[OK] Banco").And.Contain("Gestão pela Web ligada");
    }

    [Fact]
    public async Task Unchanged_items_are_counted_but_not_stored()
    {
        await _harness.RunAsync(Ct);

        var second = await _harness.RunAsync(Ct);

        second.Counts["employee"].Should().ContainKey("unchanged");
        var stored = await db.QueryAsync<int>(
            "SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @RunId AND status = 'unchanged'", new { second.RunId });
        stored.Should().Equal(0);
    }

    private async Task EnsureSchemasAsync()
    {
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await _store.EnsureSchemaAsync(Ct);
    }

    private async Task<CommandOutcome> RunCommandAsync(string type)
    {
        await EnsureSchemasAsync();
        var id = await _store.EnqueueCommandAsync("default", type, "carlos", Ct);
        await _harness.ProcessCommandsAsync(Ct);
        var rows = await db.QueryAsync<CommandOutcome>(
            "SELECT status AS Status, run_id AS RunId, resultado AS Result FROM solidesdp.comando WHERE id = @id", new { id });
        return rows.Single();
    }

    private sealed record CommandOutcome
    {
        public string Status { get; init; } = string.Empty;
        public Guid? RunId { get; init; }
        public string? Result { get; init; }
    }
}
