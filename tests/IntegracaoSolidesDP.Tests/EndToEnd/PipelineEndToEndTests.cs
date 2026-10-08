using System.Net.Http.Json;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Worker.State;
using SolidesDP.Fake.Configuration;

namespace IntegracaoSolidesDP.Tests.EndToEnd;

[Collection(SqlServerCollection.Name)]
public sealed class PipelineEndToEndTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly RhuSeed _seed = new(db);
    private E2EHarness _harness = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _seed.BasicsAsync();
        _harness = new E2EHarness(db);
        await _harness.Fake.ResetAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task First_run_creates_everything_and_second_run_writes_nothing()
    {
        await _seed.FuncionarioAsync(matric: "00000001", cpf: TestData.Cpf1);
        await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2, tipo: 2);
        await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 2);

        var first = await _harness.RunAsync(Ct);

        first.Status.Should().Be(RunStatuses.Completed, first.Error);
        var state = await _harness.Fake.GetStateAsync(Ct);
        state.JobRoles.Should().ContainSingle(j => j.ExternalId == "00100" && j.Description == "ANALISTA DE SISTEMAS");
        state.Workplaces.Should().ContainSingle(w => w.ExternalId == "1-1" && w.Name == "ADN MATRIZ (1-1)");
        state.Employees.Select(e => e.ExternalId).Should().BeEquivalentTo(["1-00000001", "1-00000002"]);
        var maria = state.Employees.Single(e => e.ExternalId == "1-00000001");
        maria.CompanyId.Should().Be(state.Companies.Single(c => c.Cnpj == "00594807000108").Id);
        maria.WorkScheduleId.Should().Be(state.WorkSchedules.Single(s => s.Standard).Id);
        maria.WorkScheduleDateInMillis.Should().Be(TestData.Dates.StartOfDay(new DateOnly(2026, 1, 1)), "admitida antes do go-live");
        var ferias = state.Adjustments.Should().ContainSingle().Subject;
        ferias.EmployeeId.Should().Be(maria.Id);
        ferias.Status.Should().Be("APROVADO");
        ferias.Observation.Should().StartWith("RHSenso:");

        await _harness.Fake.ClearRequestsAsync(Ct);
        var second = await _harness.RunAsync(Ct);

        second.Status.Should().Be(RunStatuses.Completed);
        (await _harness.WritesAsync(Ct)).Should().BeEmpty("nada mudou no RHSenso");
    }

    [Fact]
    public async Task Transfer_between_branches_updates_the_same_dp_employee()
    {
        await _seed.FilialAsync(empresa: 1, filial: 37, nome: "OUTSOURCING CETREL");
        var original = await _seed.FuncionarioAsync(matric: "00002127");
        await _harness.RunAsync(Ct);
        var before = (await _harness.Fake.GetStateAsync(Ct)).Employees.Single();

        // Como o RHSenso faz: a linha antiga vira 09 e nasce uma nova na outra filial com a mesma matrícula.
        await db.ExecuteAsync("UPDATE dbo.func1 SET cdsituacao = '09', dttransf = '2026-10-01' WHERE id = @original", new { original });
        await _seed.FuncionarioAsync(matric: "00002127", filial: 37, transferencia: new DateTime(2026, 10, 1));
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var state = await _harness.Fake.GetStateAsync(Ct);
        var after = state.Employees.Should().ContainSingle().Subject;
        after.Id.Should().Be(before.Id);
        after.Fired.Should().BeFalse();
        after.WorkplaceId.Should().Be(state.Workplaces.Single(w => w.ExternalId == "1-37").Id);
    }

    [Fact]
    public async Task Transfer_to_another_company_dismisses_the_old_bond_before_creating_the_new_one()
    {
        await _seed.FilialAsync(empresa: 2, filial: 8, nome: "GTI ABC", cnpj: "00594807000361");
        var original = await _seed.FuncionarioAsync(matric: "00002127", cpf: TestData.Cpf3);
        await _harness.RunAsync(Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);

        await db.ExecuteAsync("UPDATE dbo.func1 SET cdsituacao = '09', dttransf = '2026-10-01' WHERE id = @original", new { original });
        await _seed.FuncionarioAsync(matric: "00002127", empresa: 2, filial: 8, cpf: TestData.Cpf3);
        var summary = await _harness.RunAsync(Ct);

        // O fake recusa CPF repetido entre ativos: só passa se o desligamento vier antes.
        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var state = await _harness.Fake.GetStateAsync(Ct);
        var old = state.Employees.Single(e => e.ExternalId == "1-00002127");
        old.Fired.Should().BeTrue();
        old.ResignationReason.Should().Be("TRANSFERENCIA_GRUPO_EMPRESARIAL");
        old.ResignationDate.Should().Be(TestData.Dates.StartOfDay(new DateOnly(2026, 10, 1)));
        state.Employees.Single(e => e.ExternalId == "2-00002127").Fired.Should().BeFalse();

        var writes = await _harness.WritesAsync(Ct);
        var dismiss = writes.Single(w => w.Path == "/employee/dismiss");
        var create = writes.Single(w => w.Path == "/employee/register" && w.Body!["externalId"]!.GetValue<string>() == "2-00002127");
        dismiss.Seq.Should().BeLessThan(create.Seq);
    }

    [Fact]
    public async Task Only_people_the_integration_created_are_dismissed()
    {
        var synced = await _seed.FuncionarioAsync(matric: "00000001");
        await _seed.FuncionarioAsync(matric: "00000099", cpf: TestData.Cpf2, situacao: "08", demissao: new DateTime(2019, 5, 1), causa: "11");
        await _harness.RunAsync(Ct);

        await db.ExecuteAsync("UPDATE dbo.func1 SET cdsituacao = '08', dtdemissao = '2026-10-02', cdcausres = '21' WHERE id = @synced", new { synced });
        await _harness.Fake.ClearRequestsAsync(Ct);
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var employee = (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Subject;
        employee.Fired.Should().BeTrue();
        employee.ResignationReason.Should().Be("PEDIDO_DEMISSAO_COLABORADOR");
        (await _harness.WritesAsync(Ct)).Should().ContainSingle(w => w.Path == "/employee/dismiss");
    }

    [Fact]
    public async Task Employee_whose_company_cnpj_is_not_in_dp_is_blocked_and_others_go_through()
    {
        await _seed.FilialAsync(empresa: 99, filial: 1, nome: "SEM CNPJ NO DP", cnpj: "11222333000181");
        await _seed.FuncionarioAsync(matric: "00000001");
        await _seed.FuncionarioAsync(matric: "00000002", empresa: 99, cpf: TestData.Cpf2);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.CompletedWithErrors);
        summary.Counts["employee"]["blocked"].Should().Be(1);
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Select(e => e.ExternalId).Should().Equal("1-00000001");
    }

    [Fact]
    public async Task A_rejected_employee_does_not_stop_the_others_in_both_error_styles()
    {
        foreach (var style in new[] { ErrorStyle.ResponseEntity, ErrorStyle.Http })
        {
            await db.ResetAsync();
            await _seed.BasicsAsync();
            await _harness.Fake.ResetAsync(Ct);
            await _harness.Fake.UpdateBehaviorAsync(b => b.ErrorStyle = style, Ct);
            await _seed.FuncionarioAsync(matric: "00000001", cpf: TestData.Cpf1);
            await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2);
            await _harness.Fake.AddFaultAsync(new FaultRule { Method = "POST", Path = "/employee/register", Kind = FaultKind.ErrorInBody, Times = 1, Message = "CPF inválido na Receita" }, Ct);

            var summary = await _harness.RunAsync(Ct);

            summary.Status.Should().Be(RunStatuses.CompletedWithErrors, $"ErrorStyle={style}");
            summary.Counts["employee"]["failed"].Should().Be(1, $"ErrorStyle={style}");
            summary.Counts["employee"]["created"].Should().Be(1, $"ErrorStyle={style}");
        }
    }

    [Fact]
    public async Task Update_keeps_the_work_schedule_hr_assigned_in_dp()
    {
        await _seed.FuncionarioAsync(matric: "00000001", nome: "MARIA");
        await _harness.RunAsync(Ct);
        var state = await _harness.Fake.GetStateAsync(Ct);
        var other = state.WorkSchedules.Single(s => !s.Standard);
        // O RH troca a escala direto no DP.
        var effective = TestData.Dates.StartOfDay(new DateOnly(2026, 1, 1));
        using var hr = _harness.DpAsHr();
        var change = await hr.PostAsJsonAsync("/employee/register?allowUpdate=true", new
        {
            externalId = "1-00000001",
            name = "MARIA",
            admissionDate = TestData.Dates.StartOfDay(new DateOnly(2020, 3, 2)),
            effectiveDate = effective,
            workSchedule = other.Id,
            workScheduleDateInMillis = TestData.Dates.StartOfDay(new DateOnly(2026, 10, 1)),
            punchRuleDateInMillis = effective,
        }, Ct);
        change.IsSuccessStatusCode.Should().BeTrue();
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Single().WorkScheduleId.Should().Be(other.Id);

        await db.ExecuteAsync("UPDATE dbo.func1 SET nmcolab = 'MARIA SOUZA'");
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var employee = (await _harness.Fake.GetStateAsync(Ct)).Employees.Single();
        employee.Name.Should().Be("MARIA SOUZA");
        employee.WorkScheduleId.Should().Be(other.Id);
    }

    [Fact]
    public async Task Rate_limiting_is_retried_transparently()
    {
        await _seed.FuncionarioAsync();
        await _harness.Fake.AddFaultAsync(new FaultRule { Method = "POST", Path = "/employee/register", Kind = FaultKind.Status, Status = 429, RetryAfterSeconds = 0, Times = 2 }, Ct);

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task Invalid_token_aborts_without_writing()
    {
        await _seed.FuncionarioAsync();
        _harness.Settings["SolidesDP:Token"] = "token-errado";

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("token");
        (await _harness.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Safety_limit_on_creates_sends_nothing()
    {
        await _seed.FuncionarioAsync(matric: "00000001", cpf: TestData.Cpf1);
        await _seed.FuncionarioAsync(matric: "00000002", cpf: TestData.Cpf2);
        _harness.Settings["Sync:MaxCreatesPerRun"] = "1";

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.CompletedWithErrors);
        (await _harness.Fake.GetStateAsync(Ct)).Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_calls_nothing_on_the_api()
    {
        await _seed.FuncionarioAsync();
        await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 2);
        _harness.Settings["Sync:DryRun"] = "true";

        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        summary.Counts["employee"]["dry_run"].Should().Be(1);
        summary.Counts["vacation"]["dry_run"].Should().Be(1);
        (await _harness.Fake.GetRequestsAsync(Ct)).Should().BeEmpty();
    }
}
