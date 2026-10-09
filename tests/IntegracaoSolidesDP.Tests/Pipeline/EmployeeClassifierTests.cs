using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;

namespace IntegracaoSolidesDP.Tests.Pipeline;

public sealed class EmployeeClassifierTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static EscopoEmpresa Empresa(int cdempresa = 1, params int[] filiais) => new(cdempresa, filiais.ToHashSet());

    [Fact]
    public void The_key_is_the_cpf_and_the_label_is_empresa_matricula()
    {
        var row = TestData.Employee(matric: "00007811", empresa: 15, filial: 1);

        var plan = EmployeeClassifier.Classify([row], new SyncOptions(), Today, Empresa(15));

        plan.ActiveKeys.Should().BeEquivalentTo([TestData.Cpf1]);
        plan.LabelFor(TestData.Cpf1).Should().Be("15-00007811");
        plan.KeyByMatricula["00007811"].Should().Be(TestData.Cpf1);
    }

    [Fact]
    public void Only_rows_of_the_company_are_considered()
    {
        var mine = TestData.Employee(matric: "00000001", empresa: 15);
        var other = TestData.Employee(matric: "00000002", empresa: 1, cpf: TestData.Cpf2);

        var plan = EmployeeClassifier.Classify([mine, other], new SyncOptions(), Today, Empresa(15));

        plan.Active.Should().ContainSingle().Which.Cdempresa.Should().Be(15);
        plan.Departures.Should().BeEmpty();
    }

    [Fact]
    public void Transfer_between_branches_of_the_same_company_keeps_one_active_cpf()
    {
        // Mesmo comportamento do RHSenso: a linha antiga fica 09 e uma nova é criada na outra filial.
        var old = TestData.Employee(matric: "00002127", empresa: 2, filial: 22, situacao: "09");
        var current = TestData.Employee(matric: "00002127", empresa: 2, filial: 35, transferencia: new DateTime(2025, 4, 1));

        var plan = EmployeeClassifier.Classify([old, current], new SyncOptions(), Today, Empresa(2));

        plan.Active.Should().ContainSingle().Which.Cdfilial.Should().Be(35);
        plan.Departures.Should().BeEmpty();
    }

    [Fact]
    public void Transfer_to_another_company_departs_from_the_old_company_account()
    {
        var old = TestData.Employee(matric: "00002127", empresa: 1, filial: 8, situacao: "09", transferencia: new DateTime(2025, 4, 1));
        var current = TestData.Employee(matric: "00002127", empresa: 2, filial: 8);

        var inOld = EmployeeClassifier.Classify([old, current], new SyncOptions(), Today, Empresa(1));
        var inNew = EmployeeClassifier.Classify([old, current], new SyncOptions(), Today, Empresa(2));

        inOld.Active.Should().BeEmpty();
        inOld.Departures.Should().ContainSingle().Which.Should().Be(
            new Departure(TestData.Cpf1, "1-00002127", new DateOnly(2025, 4, 1), "TRANSFERENCIA_GRUPO_EMPRESARIAL", IsTransfer: true));
        inNew.Active.Should().ContainSingle().Which.Rotulo.Should().Be("2-00002127");
    }

    [Fact]
    public void Dismissal_uses_the_rescission_cause_map()
    {
        var row = TestData.Employee(situacao: "08", desligado: true, demissao: new DateTime(2026, 9, 30), causa: "21");

        var plan = EmployeeClassifier.Classify([row], new SyncOptions(), Today, Empresa());

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().ContainSingle().Which.Should().Be(
            new Departure(TestData.Cpf1, "1-00000001", new DateOnly(2026, 9, 30), "PEDIDO_DEMISSAO_COLABORADOR", IsTransfer: false));
    }

    [Theory]
    [InlineData("08")]
    [InlineData("11")]
    [InlineData("12")]
    [InlineData("14")]
    public void Dismissal_and_retirement_situations_depart(string situacao)
    {
        var plan = EmployeeClassifier.Classify([TestData.Employee(situacao: situacao)], new SyncOptions(), Today, Empresa());

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().ContainSingle().Which.IsTransfer.Should().BeFalse();
    }

    [Theory]
    [InlineData("02")]
    [InlineData("03")]
    [InlineData("07")]
    [InlineData("10")]
    [InlineData("15")]
    public void Leaves_stay_active(string situacao)
    {
        EmployeeClassifier.Classify([TestData.Employee(situacao: situacao)], new SyncOptions(), Today, Empresa()).Active.Should().ContainSingle();
    }

    [Fact]
    public void Configured_cause_overrides_the_default_and_unknown_causes_fall_back_to_outros()
    {
        var options = new SyncOptions { MotivoDemissaoMap = new Dictionary<string, string> { ["21"] = "DEMISSAO" } };
        var configured = TestData.Employee(matric: "1", situacao: "08", desligado: true, causa: "21");
        var unknown = TestData.Employee(matric: "2", cpf: TestData.Cpf2, situacao: "08", desligado: true, causa: "77");

        var plan = EmployeeClassifier.Classify([configured, unknown], options, Today, Empresa());

        plan.Departures.Select(d => d.Reason).Should().BeEquivalentTo(["DEMISSAO", "OUTROS"]);
    }

    [Fact]
    public void Pre_registration_rows_are_ignored()
    {
        var plan = EmployeeClassifier.Classify([TestData.Employee(situacao: "99")], new SyncOptions(), Today, Empresa());

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().BeEmpty();
    }

    [Fact]
    public void Readmission_with_a_new_matricula_keeps_the_cpf_active()
    {
        var dismissed = TestData.Employee(matric: "00000010", situacao: "08", desligado: true, demissao: new DateTime(2024, 1, 31));
        var readmitted = TestData.Employee(matric: "00000099", admissao: new DateTime(2025, 2, 1));

        var plan = EmployeeClassifier.Classify([dismissed, readmitted], new SyncOptions(), Today, Empresa());

        plan.Active.Should().ContainSingle().Which.Nomatric.Should().Be("00000099");
        plan.Departures.Should().BeEmpty();
        plan.KeyByMatricula.Should().ContainKeys("00000010", "00000099");
    }

    [Fact]
    public void Same_cpf_active_in_two_registrations_is_reported_and_not_sent()
    {
        var a = TestData.Employee(matric: "00000111", filial: 1, cpf: TestData.Cpf1);
        var b = TestData.Employee(matric: "00001039", filial: 2, cpf: "529.982.247-25");
        var c = TestData.Employee(matric: "00000200", cpf: TestData.Cpf2);

        var plan = EmployeeClassifier.Classify([a, b, c], new SyncOptions(), Today, Empresa());

        plan.Active.Select(r => r.Nomatric).Should().BeEquivalentTo(["00000200"]);
        plan.Departures.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Message.Should().StartWith("cpf_ativo_duplicado");
    }

    [Fact]
    public void Duplicate_rows_of_the_same_registration_keep_the_most_recent()
    {
        var older = TestData.Employee(admissao: new DateTime(2019, 1, 1));
        var newer = TestData.Employee(admissao: new DateTime(2024, 1, 1));

        var plan = EmployeeClassifier.Classify([older, newer], new SyncOptions(), Today, Empresa());

        plan.Active.Should().ContainSingle().Which.DataAdmissao.Should().Be(new DateTime(2024, 1, 1));
        plan.Skipped.Should().ContainSingle().Which.Message.Should().StartWith("superseded");
    }

    [Fact]
    public void Active_rows_without_a_valid_cpf_become_pending_items()
    {
        var plan = EmployeeClassifier.Classify([TestData.Employee(cpf: "123")], new SyncOptions(), Today, Empresa());

        plan.Active.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle().Which.Message.Should().StartWith("cpf_invalido");
    }

    [Fact]
    public void Active_in_an_unselected_branch_is_out_of_scope_and_never_departs()
    {
        var row = TestData.Employee(filial: 7);

        var plan = EmployeeClassifier.Classify([row], new SyncOptions(), Today, Empresa(1, 10));

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().BeEmpty();
        plan.OutOfScope.Should().ContainKey(TestData.Cpf1);
    }

    [Fact]
    public void Dismissal_is_a_fact_of_the_payroll_even_outside_the_selected_branches()
    {
        var row = TestData.Employee(filial: 7, situacao: "08", desligado: true);

        var plan = EmployeeClassifier.Classify([row], new SyncOptions(), Today, Empresa(1, 10));

        plan.Departures.Should().ContainSingle();
    }

    [Theory]
    [InlineData("1-00000002")]
    [InlineData("00000002")]
    [InlineData(TestData.Cpf2)]
    [InlineData("111.444.777-35")]
    public void Allow_list_restricts_everything_to_the_pilot(string entry)
    {
        var options = new SyncOptions { ExternalIdAllowList = [entry] };
        var rows = new[]
        {
            TestData.Employee(matric: "00000001"),
            TestData.Employee(matric: "00000002", cpf: TestData.Cpf2),
            TestData.Employee(matric: "00000003", cpf: TestData.Cpf3, situacao: "08", desligado: true),
        };

        var plan = EmployeeClassifier.Classify(rows, options, Today, Empresa());

        plan.Active.Select(r => r.Rotulo).Should().BeEquivalentTo(["1-00000002"]);
        plan.Departures.Should().BeEmpty();
    }
}
