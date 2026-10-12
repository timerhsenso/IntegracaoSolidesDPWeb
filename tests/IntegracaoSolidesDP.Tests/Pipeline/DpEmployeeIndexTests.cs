using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Pipeline.Steps;

namespace IntegracaoSolidesDP.Tests.Pipeline;

public sealed class DpEmployeeIndexTests
{
    [Fact]
    public void Indexes_active_employees_by_normalized_cpf_and_external_code()
    {
        var index = DpEmployeeIndex.From(
        [
            new EmployeeDto { Id = 1, Cpf = "529.982.247-25", ExternalId = "00007811" },
            new EmployeeDto { Id = 2, Cpf = TestData.Cpf2, ExternalId = " " },
            new EmployeeDto { Id = 3, Cpf = TestData.Cpf3, ExternalId = "00000003", Fired = true },
        ]);

        index.Count.Should().Be(2, "desligados não contam");
        index.ByCpf(TestData.Cpf1).Should().ContainSingle().Which.Id.Should().Be(1);
        index.ByCodigoExterno("00007811").Should().ContainSingle().Which.Id.Should().Be(1);
        index.ByCpf(TestData.Cpf3).Should().BeEmpty();
        index.ByCodigoExterno("00000003").Should().BeEmpty();
    }

    [Fact]
    public void Two_active_records_with_the_same_cpf_are_both_kept()
    {
        var index = DpEmployeeIndex.From(
        [
            new EmployeeDto { Id = 1, Cpf = TestData.Cpf1 },
            new EmployeeDto { Id = 2, Cpf = TestData.Cpf1 },
        ]);

        index.ByCpf(TestData.Cpf1).Should().HaveCount(2);
    }

    [Fact]
    public void Dismissed_are_the_ones_listed_with_the_fired_but_not_among_the_active()
    {
        var ativos = DpEmployeeIndex.From([new EmployeeDto { Id = 1, Cpf = TestData.Cpf1 }]);

        var desligados = DpDesligadosIndex.From(
        [
            new EmployeeDto { Id = 1, Cpf = TestData.Cpf1 },
            new EmployeeDto { Id = 2, Cpf = TestData.Cpf2, Fired = true, ResignationDate = 1_000 },
            new EmployeeDto { Id = 3, Cpf = TestData.Cpf2, ResignationDate = 2_000 },
            new EmployeeDto { Id = 4, Cpf = TestData.Cpf3, Fired = false },
        ], ativos);

        desligados.ByCpf(TestData.Cpf1).Should().BeEmpty("está ativo");
        desligados.ByCpf(TestData.Cpf2).Select(e => e.Id).Should().Equal([3L, 2L], "sem o campo fired também conta; o desligamento mais recente primeiro");
        desligados.ByCpf(TestData.Cpf3).Should().BeEmpty("fired = false e fora da lista de ativos: inconsistência da API, não é tratado como desligado");
    }
}
