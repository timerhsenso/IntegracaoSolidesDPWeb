using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;

namespace IntegracaoSolidesDP.Tests.Pipeline;

public sealed class EmployeeClassifierTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    [Fact]
    public void Transfer_between_branches_of_the_same_company_keeps_one_active_key()
    {
        // Mesmo comportamento do RHSenso: a linha antiga fica 09 e uma nova é criada na outra filial.
        var old = TestData.Employee(matric: "00002127", empresa: 2, filial: 22, situacao: "09");
        var current = TestData.Employee(matric: "00002127", empresa: 2, filial: 35, transferencia: new DateTime(2025, 4, 1));

        var plan = EmployeeClassifier.Classify([old, current], new SyncOptions(), Today);

        plan.Active.Should().ContainSingle().Which.Cdfilial.Should().Be(35);
        plan.Departures.Should().BeEmpty();
    }

    [Fact]
    public void Transfer_to_another_company_departs_the_old_key()
    {
        var old = TestData.Employee(matric: "00002127", empresa: 1, filial: 8, situacao: "09", transferencia: new DateTime(2025, 4, 1));
        var current = TestData.Employee(matric: "00002127", empresa: 2, filial: 8);

        var plan = EmployeeClassifier.Classify([old, current], new SyncOptions(), Today);

        plan.Active.Should().ContainSingle().Which.ExternalId.Should().Be("2-00002127");
        plan.Departures.Should().ContainSingle().Which.Should().Be(
            new Departure("1-00002127", new DateOnly(2025, 4, 1), "TRANSFERENCIA_GRUPO_EMPRESARIAL", IsTransfer: true));
    }

    [Fact]
    public void Dismissal_uses_the_rescission_cause_map()
    {
        var row = TestData.Employee(situacao: "08", desligado: true, demissao: new DateTime(2026, 9, 30), causa: "21");

        var plan = EmployeeClassifier.Classify([row], new SyncOptions(), Today);

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().ContainSingle().Which.Should().Be(
            new Departure(row.ExternalId, new DateOnly(2026, 9, 30), "PEDIDO_DEMISSAO_COLABORADOR", IsTransfer: false));
    }

    [Fact]
    public void Configured_cause_overrides_the_default_and_unknown_causes_fall_back_to_outros()
    {
        var options = new SyncOptions { MotivoDemissaoMap = new Dictionary<string, string> { ["21"] = "DEMISSAO" } };
        var configured = TestData.Employee(matric: "1", situacao: "08", desligado: true, causa: "21");
        var unknown = TestData.Employee(matric: "2", situacao: "08", desligado: true, causa: "77");

        var plan = EmployeeClassifier.Classify([configured, unknown], options, Today);

        plan.Departures.Select(d => d.Reason).Should().BeEquivalentTo(["DEMISSAO", "OUTROS"]);
    }

    [Fact]
    public void Employees_on_leave_stay_active()
    {
        var onLeave = TestData.Employee(situacao: "02");

        EmployeeClassifier.Classify([onLeave], new SyncOptions(), Today).Active.Should().ContainSingle();
    }

    [Fact]
    public void Pre_registration_rows_are_ignored()
    {
        var plan = EmployeeClassifier.Classify([TestData.Employee(situacao: "99")], new SyncOptions(), Today);

        plan.Active.Should().BeEmpty();
        plan.Departures.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_active_rows_keep_the_most_recent_and_report_the_rest()
    {
        var older = TestData.Employee(filial: 1, admissao: new DateTime(2019, 1, 1));
        var newer = TestData.Employee(filial: 2, admissao: new DateTime(2024, 1, 1));

        var plan = EmployeeClassifier.Classify([older, newer], new SyncOptions(), Today);

        plan.Active.Should().ContainSingle().Which.Cdfilial.Should().Be(2);
        plan.Skipped.Should().ContainSingle().Which.Message.Should().StartWith("superseded");
    }

    [Fact]
    public void Same_cpf_on_two_active_keys_is_left_out_unless_double_bind_is_allowed()
    {
        var a = TestData.Employee(matric: "00000111", cpf: TestData.Cpf1);
        var b = TestData.Employee(matric: "00001039", cpf: "529.982.247-25");
        var c = TestData.Employee(matric: "00000200", cpf: TestData.Cpf2);

        var strict = EmployeeClassifier.Classify([a, b, c], new SyncOptions(), Today);
        var lenient = EmployeeClassifier.Classify([a, b, c], new SyncOptions { AllowDoubleBind = true }, Today);

        strict.Active.Select(r => r.Nomatric).Should().BeEquivalentTo(["00000200"]);
        strict.Skipped.Should().HaveCount(2).And.OnlyContain(i => i.Message!.StartsWith("skipped_duplicate_cpf", StringComparison.Ordinal));
        lenient.Active.Should().HaveCount(3);
        lenient.DoubleBind.Should().BeEquivalentTo(["1-00000111", "1-00001039"]);
    }

    [Fact]
    public void Allow_list_restricts_everything_to_the_pilot()
    {
        var options = new SyncOptions { ExternalIdAllowList = ["1-00000002"] };
        var rows = new[]
        {
            TestData.Employee(matric: "00000001"),
            TestData.Employee(matric: "00000002", cpf: TestData.Cpf2),
            TestData.Employee(matric: "00000003", situacao: "08", desligado: true),
        };

        var plan = EmployeeClassifier.Classify(rows, options, Today);

        plan.Active.Select(r => r.ExternalId).Should().BeEquivalentTo(["1-00000002"]);
        plan.Departures.Should().BeEmpty();
    }
}
