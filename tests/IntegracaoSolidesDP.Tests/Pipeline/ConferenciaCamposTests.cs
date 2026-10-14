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

    [Fact]
    public void Workplace_comes_from_the_active_entries_of_workplace_list_as_the_real_api_returns_it()
    {
        // employee/find da API real: sem currentWorkplaceDTO, o local vem na workplaceList.
        var rh = new EmployeeRequest { Name = "MARIA", WorkplaceExternalId = "15-1" };
        var noLocal = new EmployeeDto
        {
            Id = 1, Name = "MARIA",
            WorkplaceList = [new WorkplaceDto { Id = 9, ExternalId = "15-1", Name = "ADN PROJETOS (15-1)", Active = true }],
        };
        var emOutro = noLocal with
        {
            WorkplaceList =
            [
                new WorkplaceDto { Id = 8, ExternalId = "15-1", Name = "ANTIGO", Active = false },
                new WorkplaceDto { Id = 9, ExternalId = "15-2", Name = "OBRA" },
            ],
        };

        ConferenciaCampos.Comparar(rh, noLocal, TestData.Dates).Should().BeEmpty();
        ConferenciaCampos.Comparar(rh, emOutro, TestData.Dates).Should().ContainSingle()
            .Which.Should().Be((CamposConferencia.Local, "15-1", "15-2 - OBRA"), "local inativo na lista não conta");
    }

    [Fact]
    public void Real_api_employee_find_json_is_read_with_the_fields_the_conference_compares()
    {
        // Trecho do employee/find real (cadastro da piloto, 14/10/2026, dados trocados).
        const string json = """
            {"id":4228589,"name":"MARIA","birthDate":"1970-03-23","phone":"071988694250","ctps":"4981911","series":"00040",
             "admissionDate":"2014-01-06","company":{"id":2074869},
             "jobRoleDTO":{"id":3168226,"description":"AUX. DE SERVICOS GERAIS","externalId":"005","alterationDate":1791585902316},
             "workplaceList":[{"id":2723614,"name":"ADN PROJETOS (15-1)","externalId":"15-1","active":true}],
             "effectiveDate":1722481200000,"externalId":"00007811","fired":false,"gender":"FEMININO"}
            """;

        var dp = System.Text.Json.JsonSerializer.Deserialize<EmployeeDto>(json, SolidesDpJson.Options)!;
        var rh = new EmployeeRequest
        {
            Name = "MARIA", Ctps = "4981911", Series = "00040", Phone = "071988694250", JobRoleExternalId = "005",
            WorkplaceExternalId = "15-1", BirthDate = TestData.Dates.StartOfDay(new DateOnly(1970, 3, 23)),
            AdmissionDate = TestData.Dates.StartOfDay(new DateOnly(2014, 1, 6)), Gender = "FEMININO", Company = 2074869,
        };

        ConferenciaCampos.Comparar(rh, dp, TestData.Dates).Should().BeEmpty();
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
