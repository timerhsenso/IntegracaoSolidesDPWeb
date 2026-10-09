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
    public async Task First_run_creates_version_1_from_appsettings_and_without_companies_syncs_nothing()
    {
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.SkippedDisabled);
        summary.Error.Should().Contain("Nenhuma empresa habilitada");
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        current!.Version.Should().Be(1);
        current.Empresas.Should().BeEmpty();
        (await _harness.Fake.GetRequestsAsync(Ct)).Should().BeEmpty("com a gestão ligada, só as empresas da tela Empresas são sincronizadas");
    }

    [Fact]
    public async Task Run_records_the_configuration_version()
    {
        await HabilitarEmpresa1Async();

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        var version = await db.QueryAsync<int>("SELECT config_versao FROM solidesdp.runs WHERE run_id = @RunId", new { summary.RunId });
        version.Should().Equal(current!.Version);
    }

    [Fact]
    public async Task Disabled_integration_does_not_run_but_still_allows_a_dry_run()
    {
        await HabilitarEmpresa1Async();
        await _harness.RunAsync(Ct);
        var current = await _store.GetCurrentConfigurationAsync("default", Ct);
        await _store.AddConfigurationVersionAsync("default", active: false, current!.SyncJson, "pausa", "carlos", Ct);
        await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2, nome: "JOAO SANTOS");
        await _harness.Fake.ClearRequestsAsync(Ct);

        var real = await _harness.RunAsync(Ct);
        var dryRun = await RunCommandAsync(CommandTypes.DryRun);

        real.Status.Should().Be(RunStatuses.SkippedDisabled);
        dryRun.Status.Should().Be(CommandStatuses.Done, dryRun.Result);
        (await _harness.WritesAsync(Ct)).Should().BeEmpty("a integração está desativada e a simulação não grava no Sólides DP");
    }

    [Fact]
    public async Task Simulation_comes_from_the_company_not_from_appsettings()
    {
        await HabilitarEmpresa1Async(dryRun: true);
        await _harness.Fake.ClearRequestsAsync(Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.DryRun.Should().BeTrue("a empresa está em simulação, embora o appsettings diga DryRun=false");
        (await _harness.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_general_rules_are_recorded_as_a_failed_run_without_calling_the_api()
    {
        await EnsureSchemasAsync();
        await _store.AddConfigurationVersionAsync("default", active: true, """{ "TiposColaborador": [] }""",
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false, GoLiveDate = E2EHarness.GoLive }], null, "carlos", Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("config_invalid").And.Contain("TiposColaborador");
        (await _harness.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Company_without_go_live_fails_its_real_run_without_calling_the_api()
    {
        await _harness.ConfigurarEmpresasAsync(
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false }],
            new Dictionary<int, string> { [1] = E2EHarness.Token },
            Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("config_invalid").And.Contain("go-live");
        (await _harness.Fake.GetRequestsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Company_pilot_limits_the_run_to_the_listed_people()
    {
        await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2, nome: "JOAO SANTOS");
        await _harness.ConfigurarEmpresasAsync(
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false, GoLiveDate = E2EHarness.GoLive, Piloto = ["00000002"] }],
            new Dictionary<int, string> { [1] = E2EHarness.Token },
            Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Which.ExternalId.Should().Be("00000002");
    }

    [Fact]
    public async Task Run_command_executes_the_sync_on_behalf_of_the_user()
    {
        await HabilitarEmpresa1Async();
        var command = await RunCommandAsync(CommandTypes.Run);

        command.Status.Should().Be(CommandStatuses.Done, command.Result);
        command.RunId.Should().NotBeNull();
        var run = await db.QueryAsync<(string, string)>(
            "SELECT triggered_by, solicitado_por FROM solidesdp.runs WHERE run_id = @RunId", new { command.RunId });
        run.Should().Equal(("web", "carlos"));
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle(e => e.ExternalId == "00000001");
    }

    [Fact]
    public async Task Check_config_command_stores_the_console_output()
    {
        await HabilitarEmpresa1Async();
        var command = await RunCommandAsync(CommandTypes.CheckConfig);

        command.Status.Should().Be(CommandStatuses.Done, command.Result);
        command.Result.Should().Contain("[OK] Banco").And.Contain("Gestão pela Web ligada");
    }

    [Fact]
    public async Task Unchanged_items_are_counted_but_not_stored()
    {
        await HabilitarEmpresa1Async();
        await _harness.RunAsync(Ct);

        var second = await _harness.RunAsync(Ct);

        second.Counts["employee"].Should().ContainKey("unchanged");
        var stored = await db.QueryAsync<int>(
            "SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @RunId AND status = 'unchanged'", new { second.RunId });
        stored.Should().Equal(0);
    }

    [Fact]
    public async Task Company_without_a_token_fails_its_real_run_without_calling_the_api()
    {
        await _harness.ConfigurarEmpresasAsync(
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false, GoLiveDate = E2EHarness.GoLive }],
            new Dictionary<int, string>(),
            Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("config_invalid").And.Contain("token");
        (await _harness.Fake.GetRequestsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_command_can_target_one_company()
    {
        await _seed.FilialAsync(empresa: 2, filial: 8, nome: "GTI ABC", cnpj: "00594807000361");
        await _seed.FuncionarioAsync(matric: "00000002", empresa: 2, filial: 8, cpf: TestData.Cpf2);
        await _harness.ConfigurarEmpresasAsync(
            [
                new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false, GoLiveDate = E2EHarness.GoLive },
                new EmpresaConfiguracao { Cdempresa = 2, Habilitada = true, DryRun = false, GoLiveDate = E2EHarness.GoLive },
            ],
            new Dictionary<int, string> { [1] = E2EHarness.Token, [2] = E2EHarness.Token2 },
            Ct);

        var id = await _store.EnqueueCommandAsync("default", CommandTypes.Run, "carlos", 2, Ct);
        await _harness.ProcessCommandsAsync(Ct);

        var status = await db.QueryAsync<string>("SELECT status FROM solidesdp.comando WHERE id = @id", new { id });
        status.Should().Equal(CommandStatuses.Done);
        (await _harness.Fake.GetStateAsync(E2EHarness.Token, Ct)).Employees.Should().BeEmpty("só a empresa 2 foi pedida");
        (await _harness.Fake.GetStateAsync(E2EHarness.Token2, Ct)).Employees.Should().ContainSingle(e => e.ExternalId == "00000002");
    }

    /// <summary>Empresa 1 habilitada na tela Empresas, com o token da conta padrão do fake.</summary>
    private Task HabilitarEmpresa1Async(bool dryRun = false) =>
        _harness.ConfigurarEmpresasAsync(
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = dryRun, GoLiveDate = E2EHarness.GoLive }],
            new Dictionary<int, string> { [1] = E2EHarness.Token },
            Ct);

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
