using System.Net.Http.Json;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Pipeline;

/// <summary>Identidade por CPF, Código Externo, escopo e contas por empresa, de ponta a ponta contra o fake.</summary>
public sealed class InMemoryPipelineTests : IAsyncLifetime
{
    private readonly InMemoryPipeline _pipeline = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _pipeline.Fake.ResetAsync(Ct);

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    [Fact]
    public async Task New_cpf_is_created_with_the_matricula_as_external_code_and_the_second_run_writes_nothing()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));

        var first = await _pipeline.RunAsync(ct: Ct);

        first.Status.Should().Be(RunStatuses.Completed, first.Error);
        var employee = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Subject;
        employee.ExternalId.Should().Be("00007811");
        employee.Cpf.Should().Be(TestData.Cpf1);
        var register = (await _pipeline.WritesAsync(Ct)).Single(w => w.Path == "/employee/register");
        register.Body!["matricula"]!.GetValue<string>().Should().Be("00007811");
        var link = _pipeline.State.Employee(1, TestData.Cpf1)!;
        link.Origem.Should().Be(OrigensVinculo.Criado);
        link.RemoteId.Should().Be(employee.Id);
        link.Matricula.Should().Be("00007811");

        await _pipeline.Fake.ClearRequestsAsync(Ct);
        var second = await _pipeline.RunAsync(ct: Ct);

        second.Status.Should().Be(RunStatuses.Completed, second.Error);
        (await _pipeline.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("00007811", "00007811")]
    [InlineData("00000024", "00007811")]
    [InlineData("CONTAB-55", "CONTAB-55")]
    public async Task Cpf_already_registered_by_hr_is_linked_and_updated_instead_of_created(string codigoNoDp, string esperado)
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811", nome: "MARIA DA SILVA"));
        var manual = await RegisterByHrAsync(codigoNoDp, "MARIA (MANUAL)", TestData.Cpf1);

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        summary.Counts["employee"]["adopted"].Should().Be(1);
        var employee = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Subject;
        employee.Id.Should().Be(manual);
        employee.Name.Should().Be("MARIA DA SILVA");
        employee.ExternalId.Should().Be(esperado);
        var link = _pipeline.State.Employee(1, TestData.Cpf1)!;
        link.Origem.Should().Be(OrigensVinculo.VinculadoCpf);
        link.CodigoExterno.Should().Be(esperado);
        var register = (await _pipeline.WritesAsync(Ct)).Single(w => w.Path == "/employee/register" && w.Body!["tangerinoId"] is not null);
        register.Body!["tangerinoId"]!.GetValue<long>().Should().Be(manual);
    }

    [Fact]
    public async Task Linking_keeps_the_effective_date_of_the_dp_and_dates_the_job_role_change()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        var vigenciaNoDp = TestData.Dates.StartOfDay(new DateOnly(2024, 8, 1));
        await RegisterByHrAsync("00007811", "MARIA", TestData.Cpf1, effectiveDate: vigenciaNoDp);

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var register = (await _pipeline.WritesAsync(Ct)).Single(w => w.Path == "/employee/register" && w.Body!["tangerinoId"] is not null);
        register.Body!["effectiveDate"]!.GetValue<long>().Should().Be(vigenciaNoDp, "quem já existe no DP mantém o início da vigência de lá");
        register.Body!["jobRoleExternalId"]!.GetValue<string>().Should().Be("00100");
        register.Body!["jobRoleStartDate"]!.GetValue<long>().Should().Be(TestData.Dates.StartOfDay(new DateOnly(2026, 10, 5)), "a troca de cargo vale a partir de hoje");
    }

    [Fact]
    public async Task Same_job_role_does_not_send_a_start_date_again()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811", nome: "MARIA"));
        await _pipeline.RunAsync(ct: Ct);
        _pipeline.Source.Employees[0] = _pipeline.Source.Employees[0] with { Nome = "MARIA SOUZA" };
        await _pipeline.Fake.ClearRequestsAsync(Ct);

        await _pipeline.RunAsync(ct: Ct);

        var register = (await _pipeline.WritesAsync(Ct)).Single(w => w.Path == "/employee/register");
        register.Body!["jobRoleStartDate"].Should().BeNull();
    }

    [Fact]
    public async Task Linked_employee_keeps_being_updated_by_id_even_if_hr_changes_the_external_code()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811", nome: "MARIA"));
        await _pipeline.RunAsync(ct: Ct);
        var id = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Single().Id;
        await RegisterByHrAsync("FOLHA-XYZ", "MARIA", TestData.Cpf1, tangerinoId: id);

        _pipeline.Source.Employees[0] = _pipeline.Source.Employees[0] with { Nome = "MARIA SOUZA" };
        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var employee = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Subject;
        employee.Id.Should().Be(id);
        employee.Name.Should().Be("MARIA SOUZA");
        employee.ExternalId.Should().Be("FOLHA-XYZ", "código de outro sistema não é mexido");
    }

    [Fact]
    public async Task Matricula_used_as_external_code_by_someone_else_blocks_the_creation()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        await RegisterByHrAsync("00007811", "OUTRA PESSOA", TestData.Cpf2);

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.CompletedWithErrors);
        summary.Counts["employee"]["blocked"].Should().Be(1);
        (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle().Which.Name.Should().Be("OUTRA PESSOA");
    }

    [Fact]
    public async Task Dismissal_in_the_payroll_dismisses_the_linked_dp_record()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        var manual = await RegisterByHrAsync("00007811", "MARIA", TestData.Cpf1);
        await _pipeline.RunAsync(ct: Ct);

        _pipeline.Source.Employees[0] = _pipeline.Source.Employees[0] with
        {
            Situacao = "08", SituacaoDeDesligamento = true, DataDemissao = new DateTime(2026, 10, 2), CausaRescisao = "21",
        };
        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var employee = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Single(e => e.Id == manual);
        employee.Fired.Should().BeTrue();
        employee.ResignationReason.Should().Be("PEDIDO_DEMISSAO_COLABORADOR");
        _pipeline.State.Employee(1, TestData.Cpf1)!.Status.Should().Be(EntityStatuses.Dismissed);
    }

    [Fact]
    public async Task Hr_records_without_a_payroll_counterpart_are_never_touched()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        await RegisterByHrAsync("99999999", "SO NO DP", TestData.Cpf3);

        await _pipeline.RunAsync(ct: Ct);

        var onlyInDp = (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Single(e => e.Cpf == TestData.Cpf3);
        onlyInDp.Fired.Should().BeFalse();
        onlyInDp.Name.Should().Be("SO NO DP");
    }

    [Fact]
    public async Task Dry_run_with_a_token_shows_who_would_be_linked_without_writing()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000002", cpf: TestData.Cpf2));
        var manual = await RegisterByHrAsync("", "MARIA", TestData.Cpf1);
        await _pipeline.Fake.ClearRequestsAsync(Ct);

        var summary = await _pipeline.RunAsync(dryRun: true, ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        (await _pipeline.WritesAsync(Ct)).Should().BeEmpty();
        var items = _pipeline.State.Items[summary.RunId].Where(i => i.EntityType == EntityTypes.Employee).ToList();
        items.Single(i => i.ExternalId == "1-00007811").Message.Should().StartWith($"vincular pelo CPF ao cadastro {manual} do DP (Código Externo vazio → 00007811)");
        items.Single(i => i.ExternalId == "1-00000002").Message.Should().StartWith("criar (CPF não está ativo no DP)");
    }

    [Fact]
    public async Task Active_in_a_branch_that_is_not_selected_is_neither_sent_nor_dismissed()
    {
        _pipeline.Source.Workplaces.Add(new WorkplaceRow { Cdempresa = 1, Cdfilial = 2, NomeFantasia = "FILIAL 2", Cnpj = "00594807000108", Ativa = true });
        _pipeline.Management.Configure(
            [new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false, Filiais = [1] }],
            new Dictionary<int, string> { [1] = InMemoryPipeline.Token },
            Rules());
        _pipeline.Settings["Gestao:Habilitada"] = "true";
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811", filial: 1));
        await _pipeline.RunAsync(ct: Ct);

        // Transferida para a filial 2, que não está marcada: a linha antiga vira 09.
        _pipeline.Source.Employees[0] = _pipeline.Source.Employees[0] with { Situacao = "09" };
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811", filial: 2, transferencia: new DateTime(2026, 10, 1)));
        await _pipeline.Fake.ClearRequestsAsync(Ct);
        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        (await _pipeline.WritesAsync(Ct)).Should().BeEmpty();
        _pipeline.State.Items[summary.RunId].Should().ContainSingle().Which.Message.Should().StartWith("fora_do_escopo");
    }

    [Fact]
    public async Task Inactive_branch_counts_as_out_of_scope()
    {
        _pipeline.Source.Workplaces[0] = _pipeline.Source.Workplaces[0] with { Ativa = false };
        _pipeline.Source.Employees.Add(TestData.Employee());

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().StartWith("sem_filial_ativa");
        (await _pipeline.WritesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_company_writes_only_in_its_own_account()
    {
        _pipeline.Source.Empresas.Add(new EmpresaRow { Cdempresa = 15, Nome = "ADN 15" });
        _pipeline.Source.Workplaces.Add(new WorkplaceRow { Cdempresa = 15, Cdfilial = 1, NomeFantasia = "MATRIZ 15", Cnpj = "00594807000361", Ativa = true });
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", empresa: 1));
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000002", empresa: 15, cpf: TestData.Cpf2));
        _pipeline.Management.Configure(
            [
                new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false },
                new EmpresaConfiguracao { Cdempresa = 15, Habilitada = true, DryRun = false },
            ],
            new Dictionary<int, string> { [1] = InMemoryPipeline.Token, [15] = InMemoryPipeline.Token2 },
            Rules());
        _pipeline.Settings["Gestao:Habilitada"] = "true";

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        summary.Empresas.Select(e => e.Cdempresa).Should().Equal(1, 15);
        _pipeline.State.Runs.Select(r => r.Cdempresa).Should().Equal(1, 15);
        (await _pipeline.Fake.GetStateAsync(InMemoryPipeline.Token, Ct)).Employees.Should().ContainSingle().Which.ExternalId.Should().Be("00000001");
        (await _pipeline.Fake.GetStateAsync(InMemoryPipeline.Token2, Ct)).Employees.Should().ContainSingle().Which.ExternalId.Should().Be("00000002");
        (await _pipeline.Fake.GetStateAsync(InMemoryPipeline.Token2, Ct)).Workplaces.Should().ContainSingle().Which.ExternalId.Should().Be("15-1");
    }

    [Fact]
    public async Task Company_without_a_token_fails_alone_and_the_others_still_run()
    {
        _pipeline.Source.Empresas.Add(new EmpresaRow { Cdempresa = 15, Nome = "ADN 15" });
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", empresa: 1));
        _pipeline.Management.Configure(
            [
                new EmpresaConfiguracao { Cdempresa = 1, Habilitada = true, DryRun = false },
                new EmpresaConfiguracao { Cdempresa = 15, Habilitada = true, DryRun = false },
            ],
            new Dictionary<int, string> { [1] = InMemoryPipeline.Token },
            Rules());
        _pipeline.Settings["Gestao:Habilitada"] = "true";

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.CompletedWithErrors);
        summary.Empresas.Single(e => e.Cdempresa == 15).Error.Should().Contain("não tem token");
        (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task Disabled_company_and_company_inactive_in_the_payroll_are_skipped()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", empresa: 1));
        _pipeline.Management.Configure(
            [
                new EmpresaConfiguracao { Cdempresa = 1, Habilitada = false, DryRun = false },
                new EmpresaConfiguracao { Cdempresa = 20, Habilitada = true, DryRun = false },
            ],
            new Dictionary<int, string> { [1] = InMemoryPipeline.Token, [20] = InMemoryPipeline.Token2 },
            Rules());
        _pipeline.Settings["Gestao:Habilitada"] = "true";

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.SkippedDisabled);
        summary.Cdempresa.Should().Be(20);
        summary.Error.Should().Contain("inativa");
        (await _pipeline.Fake.GetRequestsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_companies_configured_more_than_one_company_in_appsettings_is_refused()
    {
        _pipeline.Settings["Sync:EmpresasIncluidas:1"] = "15";
        _pipeline.Source.Employees.Add(TestData.Employee());

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Failed);
        summary.Error.Should().Contain("exatamente uma empresa");
        (await _pipeline.Fake.GetRequestsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Vacations_are_sent_to_the_employee_found_by_cpf()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00007811"));
        var manual = await RegisterByHrAsync("00007811", "MARIA", TestData.Cpf1);
        _pipeline.Source.Vacations.Add(new VacationRow
        {
            Id = Guid.NewGuid(), Nomatric = "00007811", Cdempresa = 1, Cdfilial = 1,
            Inicio = new DateTime(2026, 10, 19), Fim = new DateTime(2026, 10, 30), Dias = 12, Situacao = 2,
        });

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        (await _pipeline.Fake.GetStateAsync(Ct)).Adjustments.Should().ContainSingle().Which.EmployeeId.Should().Be(manual);
    }

    private static string Rules() =>
        SyncOptionsJson.Serialize(new SyncOptions { DryRun = false, GoLiveDate = new DateOnly(2026, 1, 1) });

    private async Task<long> RegisterByHrAsync(string codigoExterno, string nome, string cpf, long? tangerinoId = null, long? effectiveDate = null)
    {
        using var hr = _pipeline.DpAsHr();
        var effective = effectiveDate ?? TestData.Dates.StartOfDay(new DateOnly(2026, 1, 1));
        var response = await hr.PostAsJsonAsync("/employee/register?allowUpdate=true", new
        {
            tangerinoId,
            externalId = codigoExterno,
            name = nome,
            cpf,
            admissionDate = TestData.Dates.StartOfDay(new DateOnly(2020, 3, 2)),
            effectiveDate = effective,
            workScheduleDateInMillis = effective,
            punchRuleDateInMillis = effective,
        }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return (await _pipeline.Fake.GetStateAsync(Ct)).Employees.Single(e => e.Cpf == cpf).Id;
    }
}
