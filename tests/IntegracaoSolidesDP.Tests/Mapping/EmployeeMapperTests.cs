using IntegracaoSolidesDP.Worker.Mapping;

namespace IntegracaoSolidesDP.Tests.Mapping;

public sealed class EmployeeMapperTests
{
    private readonly EmployeeMapper _mapper = new(TestData.Dates);

    [Fact]
    public void Maps_func1_columns_to_the_employee_dto()
    {
        var row = TestData.Employee(matric: "00001234", empresa: 14, filial: 8, tipo: 1);

        var payload = _mapper.Map(row, goLiveDate: null, companyId: 3011).Payload!;

        payload.ExternalId.Should().Be("14-00001234");
        payload.Matricula.Should().Be("00001234");
        payload.Name.Should().Be("MARIA DA SILVA");
        payload.Cpf.Should().Be(TestData.Cpf1);
        payload.Pis.Should().Be(TestData.Pis1);
        payload.Gender.Should().Be("FEMININO");
        payload.MaritalStatus.Should().Be("SOLTEIRO");
        payload.EducationLevel.Should().Be("SUPERIOR_COMPLETO");
        payload.RaceColor.Should().Be("PARDA");
        payload.Email.Should().Be("maria@adn.com.br");
        payload.CorporateEmail.Should().Be("maria@adn.com.br");
        payload.PersonalEmail.Should().Be("maria@gmail.com");
        payload.Phone.Should().Be("71999990000");
        payload.JobRoleExternalId.Should().Be("00100");
        payload.WorkplaceExternalId.Should().Be("14-8");
        payload.Company.Should().Be(3011);
        payload.CostCenter.Should().Be("00080 - Despesas Corporativas");
        payload.Intern.Should().BeFalse();
        payload.TypeOfLaborRelationship.Should().Be("CLT");
    }

    [Fact]
    public void Dates_are_local_midnight_in_bahia_as_epoch_milliseconds()
    {
        var payload = _mapper.Map(TestData.Employee(admissao: new DateTime(2020, 3, 2)), null, null).Payload!;

        // 02/03/2020 00:00 em America/Bahia (UTC-3) = 03:00Z.
        payload.AdmissionDate.Should().Be(new DateTimeOffset(2020, 3, 2, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        payload.BirthDate.Should().Be(new DateTimeOffset(1990, 5, 17, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Effective_date_is_the_go_live_for_people_admitted_before_it()
    {
        var goLive = new DateOnly(2026, 11, 1);

        var veteran = _mapper.Map(TestData.Employee(admissao: new DateTime(2015, 1, 5)), goLive, null).Payload!;
        var newcomer = _mapper.Map(TestData.Employee(admissao: new DateTime(2026, 12, 10)), goLive, null).Payload!;

        veteran.EffectiveDate.Should().Be(TestData.Dates.StartOfDay(goLive));
        veteran.AdmissionDate.Should().Be(TestData.Dates.StartOfDay(new DateOnly(2015, 1, 5)));
        newcomer.EffectiveDate.Should().Be(newcomer.AdmissionDate);
    }

    [Fact]
    public void Interns_are_flagged()
    {
        var payload = _mapper.Map(TestData.Employee(tipo: 2), null, null).Payload!;

        payload.Intern.Should().BeTrue();
        payload.TypeOfLaborRelationship.Should().Be("ESTAGIO");
    }

    [Theory]
    [InlineData("C", "CASADO")]
    [InlineData("D", "SEPARADO")]
    [InlineData("I", "DIVORCIADO")]
    [InlineData("V", "VIUVO")]
    public void Marital_status_follows_rhsenso_table_06(string code, string expected)
    {
        var row = TestData.Employee() with { EstadoCivil = code };

        _mapper.Map(row, null, null).Payload!.MaritalStatus.Should().Be(expected);
    }

    [Fact]
    public void Unknown_or_other_codes_are_omitted_with_a_warning_but_do_not_block()
    {
        var row = TestData.Employee() with { EstadoCivil = "O", Sexo = "X", GrauInstrucao = "99", Raca = 5 };

        var mapping = _mapper.Map(row, null, null);

        mapping.IsValid.Should().BeTrue();
        mapping.Payload!.MaritalStatus.Should().BeNull();
        mapping.Payload.Gender.Should().BeNull();
        mapping.Warnings.Should().Contain(["unknown_gender:X", "unknown_education_level:99", "unknown_race_color:5"]);
        mapping.Warnings.Should().NotContain(w => w.StartsWith("unknown_marital_status", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_name_admission_or_job_role_make_the_row_invalid()
    {
        var row = TestData.Employee(nome: "  ") with { DataAdmissao = null, Cargo = null };

        var mapping = _mapper.Map(row, null, null);

        mapping.IsValid.Should().BeFalse();
        mapping.Errors.Should().BeEquivalentTo(["missing_name", "missing_admission_date", "missing_job_role"]);
    }

    [Fact]
    public void Cpf_truncated_by_a_mask_in_rhsenso_is_omitted_and_reported()
    {
        var mapping = _mapper.Map(TestData.Employee(cpf: "529.982.247"), null, null);

        mapping.IsValid.Should().BeTrue();
        mapping.Payload!.Cpf.Should().BeNull();
        mapping.Warnings.Should().ContainSingle(w => w.StartsWith("cpf_truncado_no_rhsenso", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_row_always_hashes_the_same_and_any_change_changes_the_hash()
    {
        var row = TestData.Employee();
        var first = PayloadHasher.Hash(_mapper.Map(row, null, 1).Payload);
        var again = PayloadHasher.Hash(_mapper.Map(row, null, 1).Payload);
        var changed = PayloadHasher.Hash(_mapper.Map(row with { Cdfilial = 2 }, null, 1).Payload);

        again.Should().Be(first);
        changed.Should().NotBe(first);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
