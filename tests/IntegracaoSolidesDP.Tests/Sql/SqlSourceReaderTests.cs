using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlSourceReaderTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly SqlSourceReader _reader = new(db.Connections);
    private readonly RhuSeed _seed = new(db);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _seed.BasicsAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Reads_employees_trimmed_and_joined_with_cost_center_cnpj_and_situation()
    {
        await _seed.FuncionarioAsync(matric: "00001234", nome: "JOAO");
        await _seed.FuncionarioAsync(matric: "00009999", situacao: "08", demissao: new DateTime(2025, 1, 31), causa: "21");

        var rows = await _reader.ReadEmployeesAsync([1, 2], [], Ct);

        var joao = rows.Single(r => r.Nomatric == "00001234");
        joao.Nome.Should().Be("JOAO");
        joao.CentroCustoDescricao.Should().Be("Despesas Corporativas");
        joao.Cnpj.Should().Be("00594807000108");
        joao.SituacaoDeDesligamento.Should().BeFalse();
        joao.ExternalId.Should().Be("1-00001234");
        rows.Single(r => r.Nomatric == "00009999").SituacaoDeDesligamento.Should().BeTrue();
    }

    [Fact]
    public async Task Filters_by_collaborator_type_and_company()
    {
        await _seed.FilialAsync(empresa: 2, filial: 1, cnpj: "00594807000361");
        await _seed.FuncionarioAsync(matric: "1", tipo: 1);
        await _seed.FuncionarioAsync(matric: "2", tipo: 14);
        await _seed.FuncionarioAsync(matric: "3", tipo: 2, empresa: 2);

        (await _reader.ReadEmployeesAsync([1, 2], [], Ct)).Select(r => r.Nomatric).Should().BeEquivalentTo(["1", "3"]);
        (await _reader.ReadEmployeesAsync([1, 2], [2], Ct)).Select(r => r.Nomatric).Should().BeEquivalentTo(["3"]);
    }

    [Fact]
    public async Task Reads_only_requested_job_roles_and_all_workplaces()
    {
        await _seed.CargoAsync("00200", "TECNICO");
        await _seed.FilialAsync(empresa: 1, filial: 37, nome: "OUTSOURCING CETREL");

        (await _reader.ReadJobRolesAsync(["00200"], Ct)).Should().ContainSingle().Which.Descricao.Should().Be("TECNICO");
        (await _reader.ReadWorkplacesAsync(Ct)).Select(w => w.ExternalId).Should().BeEquivalentTo(["1-1", "1-37"]);
    }

    [Fact]
    public async Task Reads_vacations_ending_inside_the_window_and_checks_existence()
    {
        var recent = await _seed.FeriasAsync("00000001", new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        var old = await _seed.FeriasAsync("00000001", new DateTime(2024, 1, 2), new DateTime(2024, 1, 31));

        var rows = await _reader.ReadVacationsEndingFromAsync(new DateOnly(2026, 8, 1), Ct);
        var existing = await _reader.ExistingVacationIdsAsync([recent, old, Guid.NewGuid()], Ct);

        rows.Should().ContainSingle().Which.Id.Should().Be(recent);
        existing.Should().BeEquivalentTo([recent, old]);
    }
}
