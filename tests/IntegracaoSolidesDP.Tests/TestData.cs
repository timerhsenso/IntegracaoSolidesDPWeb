using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Tests;

internal static class TestData
{
    public static readonly TimeZoneInfo Bahia = TimeZoneInfo.FindSystemTimeZoneById("America/Bahia");
    public static readonly EpochDates Dates = new(Bahia);

    /// <summary>CPFs e PIS sintéticos com dígito verificador válido.</summary>
    public const string Cpf1 = "52998224725";
    public const string Cpf2 = "11144477735";
    public const string Cpf3 = "39053344705";
    public const string Pis1 = "12345678919";

    public static EmployeeRow Employee(
        string matric = "00000001",
        int empresa = 1,
        int filial = 1,
        string situacao = "01",
        bool desligado = false,
        int tipo = 1,
        string? cpf = Cpf1,
        string cargo = "00100",
        DateTime? admissao = null,
        DateTime? demissao = null,
        DateTime? transferencia = null,
        string? causa = null,
        string? nome = "MARIA DA SILVA") => new()
    {
        Id = Guid.NewGuid(),
        Nomatric = matric,
        Cdempresa = empresa,
        Cdfilial = filial,
        Nome = nome,
        TipoColaborador = tipo,
        Situacao = situacao,
        SituacaoDeDesligamento = desligado,
        DataAdmissao = admissao ?? new DateTime(2020, 3, 2),
        DataDemissao = demissao,
        DataTransferencia = transferencia,
        CausaRescisao = causa,
        Cpf = cpf,
        Pis = Pis1,
        Ctps = "1234567",
        SerieCtps = "0012",
        DataNascimento = new DateTime(1990, 5, 17),
        Sexo = "F",
        EstadoCivil = "S",
        GrauInstrucao = "09",
        Raca = 8,
        Email = "maria@adn.com.br",
        EmailAlternativo = "maria@gmail.com",
        Ddd = "71",
        Telefone = "99999-0000",
        NomeMae = "ANA DA SILVA",
        NomePai = "JOSE DA SILVA",
        Cargo = cargo,
        CentroCusto = "00080",
        CentroCustoDescricao = "Despesas Corporativas",
        Cnpj = "00594807000108",
    };

    public static EmployeeRequest FullEmployeeRequest()
    {
        var mapping = new EmployeeMapper(Dates).Map(Employee(), new DateOnly(2026, 11, 1), companyId: 3001);
        return mapping.Payload! with
        {
            WorkSchedule = 1001,
            WorkScheduleDateInMillis = mapping.Payload!.EffectiveDate,
            PunchRuleExternalId = "REGRA-PADRAO",
            PunchRuleDateInMillis = mapping.Payload!.EffectiveDate,
        };
    }
}
