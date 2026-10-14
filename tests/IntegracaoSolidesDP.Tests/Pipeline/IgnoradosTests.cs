using System.Net.Http.Json;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Pipeline;

/// <summary>"Só no Sólides DP" na conferência e os ignorados: somem da conferência e da simulação; quem entra na folha sai da lista.</summary>
public sealed class IgnoradosTests : IAsyncLifetime
{
    private readonly InMemoryPipeline _pipeline = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _pipeline.Fake.ResetAsync(Ct);

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    [Fact]
    public async Task Conference_lists_who_is_only_in_dp_and_the_ignored_ones_disappear()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA"));
        (await _pipeline.RunAsync(ct: Ct)).Status.Should().Be(RunStatuses.Completed);
        var cleber = await RegisterByHrAsync("", "CLEBER COORDENADOR", TestData.Cpf3);
        var outro = await RegisterByHrAsync("", "OUTRO SO NO DP", TestData.Cpf4);

        var antes = (await _pipeline.ConferirAsync(ct: Ct)).Single();

        antes.Erro.Should().BeNull();
        var soNoDp = antes.Linhas.Where(l => l.Campo == CamposConferencia.SoNoDp).ToList();
        soNoDp.Select(l => l.CadastroDp).Should().BeEquivalentTo(new long?[] { cleber, outro });
        soNoDp.Single(l => l.CadastroDp == cleber).Should().Match<DivergenciaCampo>(l =>
            l.Nome == "CLEBER COORDENADOR" && l.Cpf == TestData.Cpf3 && l.CpfMascarado == "390.***.***-05"
            && l.RhSenso!.Contains("CPF não existe no RHSenso", StringComparison.Ordinal) && l.Dp!.StartsWith("ativo", StringComparison.Ordinal));
        antes.Pessoas.Should().Be(2, "Maria está igual; os dois só no DP contam");

        (await _pipeline.Ignorados.IgnorarAsync(1, [new NovoIgnorado(TestData.Cpf3, cleber, "CLEBER COORDENADOR")], "carlos", Ct)).Should().Be(1);
        var depois = (await _pipeline.ConferirAsync(ct: Ct)).Single();

        depois.Linhas.Should().ContainSingle().Which.CadastroDp.Should().Be(outro);
        // O RH cadastrou dois direto no DP; da integração, só o envio da Maria: a conferência só lê o Sólides DP.
        (await _pipeline.WritesAsync(Ct)).Where(w => w.Path == "/employee/register").Should().HaveCount(3);
    }

    [Fact]
    public async Task Ignored_person_who_joins_the_payroll_leaves_the_list_and_follows_the_normal_rules()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA"));
        (await _pipeline.RunAsync(ct: Ct)).Status.Should().Be(RunStatuses.Completed);
        var cleber = await RegisterByHrAsync("", "CLEBER COORDENADOR", TestData.Cpf3);
        await _pipeline.Ignorados.IgnorarAsync(1, [new NovoIgnorado(TestData.Cpf3, cleber, "CLEBER COORDENADOR")], "carlos", Ct);

        // Entrou na folha como um tipo que a integração não leva (ex.: estagiário fora dos tipos).
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000003", cpf: TestData.Cpf3, nome: "CLEBER", tipo: 3));
        var conferencia = (await _pipeline.ConferirAsync(ct: Ct)).Single();

        _pipeline.Ignorados.Todos.Should().ContainSingle().Which.Should().Match<ColaboradorIgnorado>(i =>
            !i.Vigente && i.RemovidoPor == SqlIgnoradoStore.Servico && i.MotivoRemocao!.Contains("1-00000003", StringComparison.Ordinal));
        conferencia.Linhas.Should().ContainSingle().Which.Should().Match<DivergenciaCampo>(l =>
            l.Campo == CamposConferencia.SoNoDp && l.CadastroDp == cleber
            && l.RhSenso!.Contains("tipo de colaborador 3, fora dos tipos da integração", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ignored_person_who_joins_the_payroll_as_employee_is_linked_by_cpf_and_the_run_says_so()
    {
        var cleber = await RegisterByHrAsync("", "CLEBER COORDENADOR", TestData.Cpf3);
        await _pipeline.Ignorados.IgnorarAsync(1, [new NovoIgnorado(TestData.Cpf3, cleber, "CLEBER COORDENADOR")], "carlos", Ct);
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000003", cpf: TestData.Cpf3, nome: "CLEBER COORDENADOR"));

        var summary = await _pipeline.RunAsync(ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        _pipeline.State.Items[summary.RunId].Should().Contain(i =>
            i.Status == ItemStatuses.Warning && i.Message!.StartsWith("ignorado_voltou:", StringComparison.Ordinal) && i.ExternalId == "1-00000003");
        _pipeline.State.Employee(1, TestData.Cpf3)!.RemoteId.Should().Be(cleber, "vinculado pelo CPF, sem criar outro cadastro");
        _pipeline.Ignorados.Todos.Should().OnlyContain(i => !i.Vigente);
    }

    [Fact]
    public async Task Simulation_report_leaves_out_the_ignored_and_says_how_many()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA DA SILVA"));
        await RegisterByHrAsync("00000001", "MARIA DA SILVA", TestData.Cpf1);
        var cleber = await RegisterByHrAsync("", "CLEBER COORDENADOR", TestData.Cpf3);
        await RegisterByHrAsync("", "OUTRO SO NO DP", TestData.Cpf4);
        await _pipeline.Ignorados.IgnorarAsync(1, [new NovoIgnorado(TestData.Cpf3, cleber, "CLEBER COORDENADOR")], "carlos", Ct);

        var summary = await _pipeline.RunAsync(dryRun: true, ct: Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        _pipeline.State.Items[summary.RunId].Should().ContainSingle(i => i.Status == ItemStatuses.Warning)
            .Which.Message.Should().StartWith("so_no_dp: 1 cadastro(s)").And.Contain("Outros 1 estão ignorados na conferência");
        var relatorio = _pipeline.State.Reports[(summary.RunId, VinculacaoReport.Tipo)].Conteudo;
        relatorio.Should().Contain("OUTRO SO NO DP").And.NotContain("CLEBER");
    }

    [Fact]
    public async Task With_a_pilot_the_conference_does_not_list_who_is_only_in_dp()
    {
        _pipeline.Settings["Sync:ExternalIdAllowList:0"] = "00000001";
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA"));
        (await _pipeline.RunAsync(ct: Ct)).Status.Should().Be(RunStatuses.Completed);
        await RegisterByHrAsync("", "CLEBER COORDENADOR", TestData.Cpf3);

        var conferencia = (await _pipeline.ConferirAsync(ct: Ct)).Single();

        conferencia.Linhas.Should().BeEmpty("fora do piloto todo mundo pareceria \"só no Sólides DP\"");
    }

    private async Task<long> RegisterByHrAsync(string codigoExterno, string nome, string cpf)
    {
        using var hr = _pipeline.DpAsHr();
        var effective = TestData.Dates.StartOfDay(new DateOnly(2026, 1, 1));
        var response = await hr.PostAsJsonAsync("/employee/register?allowUpdate=true", new
        {
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
