using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.Scheduling;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Pipeline;

/// <summary>Conferência DP x RHSenso: o que conta como diferença, quando a semanal roda e o pipeline contra o fake.</summary>
public sealed class ConferenciaCamposTests : IAsyncLifetime
{
    private static readonly long Nascimento = TestData.Dates.StartOfDay(new DateOnly(1990, 5, 10));

    private readonly InMemoryPipeline _pipeline = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _pipeline.Fake.ResetAsync(Ct);

    public async ValueTask DisposeAsync() => await _pipeline.DisposeAsync();

    [Fact]
    public void Same_data_written_differently_is_not_a_difference()
    {
        var rh = new EmployeeRequest
        {
            Name = "MARIA  DA SILVA", Pis = "123.45678.90-1", Ctps = "1234567", Series = "001-a", BirthDate = Nascimento,
            Email = "Maria@ADN.com.br", Phone = "(71) 99999-0000", Gender = "FEMININO", JobRoleExternalId = "00100", WorkplaceExternalId = "15-1",
            Company = 3001,
        };
        var dp = new EmployeeDto
        {
            Id = 1, Name = "Maria da Silva", Pis = "12345678901", Ctps = "1234567", Series = "001A",
            // Meia-noite UTC do mesmo dia: a outra leitura que o DP pode devolver.
            BirthDate = new DateTimeOffset(1990, 5, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Email = "maria@adn.com.br", Phone = "71999990000", Gender = "feminino",
            JobRole = new JobRoleDto { ExternalId = "00100" }, CurrentWorkplace = new WorkplaceDto { ExternalId = "15-1" },
            Company = new CompanyDto { Id = 3001 },
        };

        ConferenciaCampos.Comparar(rh, dp, TestData.Dates).Should().BeEmpty();
    }

    [Fact]
    public void Each_different_field_is_reported_with_both_values_and_empty_rhsenso_fields_are_skipped()
    {
        var rh = new EmployeeRequest
        {
            Name = "MARIA SOUZA", Pis = "12345678901", BirthDate = Nascimento, JobRoleExternalId = "00200", WorkplaceExternalId = "15-2",
            Company = 3001, Email = null, Phone = "",
        };
        var dp = new EmployeeDto
        {
            Id = 1, Name = "MARIA", Pis = "10987654321", BirthDate = TestData.Dates.StartOfDay(new DateOnly(1990, 5, 11)),
            Email = "outro@adn.com.br", Phone = "7133330000",
            JobRole = new JobRoleDto { ExternalId = "00100", Description = "ANALISTA" },
            CurrentWorkplace = new WorkplaceDto { ExternalId = "15-1", Name = "MATRIZ" },
            Company = new CompanyDto { Id = 3002, FantasyName = "ADN FILIAL" },
        };
        var empresas = new Dictionary<long, CompanyDto> { [3001] = new() { Id = 3001, FantasyName = "ADN" } };

        var diferencas = ConferenciaCampos.Comparar(rh, dp, TestData.Dates, empresas);

        diferencas.Should().BeEquivalentTo(new (string, string?, string?)[]
        {
            (CamposConferencia.Nome, "MARIA SOUZA", "MARIA"),
            (CamposConferencia.Pis, "12345678901", "10987654321"),
            (CamposConferencia.Nascimento, "10/05/1990", "11/05/1990"),
            (CamposConferencia.Cargo, "00200", "00100 - ANALISTA"),
            (CamposConferencia.Local, "15-2", "15-1 - MATRIZ"),
            (CamposConferencia.Empresa, "3001 - ADN", "3002 - ADN FILIAL"),
        }, "e-mail e telefone vazios no RHSenso não são enviados, então não são comparados");
    }

    [Theory]
    [InlineData("2026-10-12T05:59:00-03:00", "2026-10-05T06:00:00-03:00")] // segunda antes das 6h: vale a semana anterior
    [InlineData("2026-10-12T06:00:00-03:00", "2026-10-12T06:00:00-03:00")]
    [InlineData("2026-10-15T23:00:00-03:00", "2026-10-12T06:00:00-03:00")] // quinta: a de segunda
    [InlineData("2026-10-18T10:00:00-03:00", "2026-10-12T06:00:00-03:00")] // domingo
    public void Weekly_slot_is_the_latest_configured_day_and_hour_already_passed(string agora, string esperado)
    {
        var marco = ConferenciaAgendada.UltimoMarco(DateTimeOffset.Parse(agora, System.Globalization.CultureInfo.InvariantCulture),
            DayOfWeek.Monday, 6, TestData.Dates.Zone);

        marco.Should().Be(DateTimeOffset.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Right_after_a_sync_nothing_differs_and_a_change_in_rhsenso_not_yet_sent_shows_up()
    {
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA"));
        (await _pipeline.RunAsync(ct: Ct)).Status.Should().Be(RunStatuses.Completed);

        var igual = (await _pipeline.ConferirAsync(ct: Ct)).Should().ContainSingle().Subject;

        igual.Erro.Should().BeNull();
        igual.Conferidos.Should().Be(1);
        igual.Linhas.Should().BeEmpty("o que acabou de ser enviado tem de bater com o que o DP devolve");

        _pipeline.Source.Employees.Clear();
        _pipeline.Source.Employees.Add(TestData.Employee(matric: "00000001", nome: "MARIA SOUZA"));
        var diferente = (await _pipeline.ConferirAsync(ct: Ct)).Single();

        diferente.Pessoas.Should().Be(1);
        diferente.Linhas.Should().ContainSingle().Which.Should().Match<DivergenciaCampo>(l =>
            l.Campo == CamposConferencia.Nome && l.RhSenso == "MARIA SOUZA" && l.Dp == "MARIA"
            && l.Rotulo == "1-00000001" && l.Cpf == TestData.Cpf1 && l.CpfMascarado != TestData.Cpf1);
        _pipeline.Conferencias.Salvas.Should().HaveCount(2);
        (await _pipeline.WritesAsync(Ct)).Where(w => w.Path == "/employee/register").Should().HaveCount(1, "a conferência só lê o Sólides DP");
    }
}
