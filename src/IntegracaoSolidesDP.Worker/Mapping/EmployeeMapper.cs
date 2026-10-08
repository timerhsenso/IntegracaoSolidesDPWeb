using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Mapping;

/// <summary>Resultado do mapeamento de um colaborador: payload (sem escala/regra) ou motivos de recusa.</summary>
public sealed record EmployeeMapping(EmployeeRequest? Payload, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Payload is not null && Errors.Count == 0;
}

/// <summary>
/// func1 → EmployeeDTO. Escala e regra de ponto ficam de fora daqui (e do hash): na criação
/// usam os padrões do config; na atualização preservam o que estiver no DP.
/// </summary>
public sealed class EmployeeMapper(EpochDates dates)
{
    public EmployeeMapping Map(EmployeeRow row, DateOnly? goLiveDate, long? companyId)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var name = Clean(row.Nome);
        if (name is null)
        {
            errors.Add("missing_name");
        }

        if (row.DataAdmissao is null)
        {
            errors.Add("missing_admission_date");
        }

        if (string.IsNullOrWhiteSpace(row.Cargo))
        {
            errors.Add("missing_job_role");
        }

        var (cpf, cpfWarning) = Documents.NormalizeCpf(row.Cpf);
        if (cpfWarning is not null)
        {
            warnings.Add(cpfWarning);
        }

        var (pis, pisWarning) = Documents.NormalizePis(row.Pis);
        if (pisWarning is not null)
        {
            warnings.Add(pisWarning);
        }

        if (errors.Count > 0)
        {
            return new EmployeeMapping(null, errors, warnings);
        }

        var admission = DateOnly.FromDateTime(row.DataAdmissao!.Value);
        var effective = goLiveDate is { } live && live > admission ? live : admission;
        var corporateEmail = Clean(row.Email);
        var personalEmail = Clean(row.EmailAlternativo);

        var payload = new EmployeeRequest
        {
            ExternalId = row.ExternalId,
            Name = name,
            Matricula = row.Nomatric.Trim(),
            Cpf = cpf,
            Pis = pis,
            Ctps = Clean(row.Ctps),
            Series = Clean(row.SerieCtps),
            BirthDate = dates.StartOfDay(row.DataNascimento),
            Email = corporateEmail ?? personalEmail,
            CorporateEmail = corporateEmail,
            PersonalEmail = personalEmail,
            Phone = Phone(row.Ddd, row.Telefone),
            Gender = Lookup(CodeMaps.Gender, row.Sexo, "gender", warnings),
            MaritalStatus = Lookup(CodeMaps.MaritalStatus, row.EstadoCivil, "marital_status", warnings, ignore: "O"),
            EducationLevel = Lookup(CodeMaps.EducationLevel, row.GrauInstrucao, "education_level", warnings),
            RaceColor = row.Raca is { } raca
                ? CodeMaps.RaceColor.GetValueOrDefault(raca) ?? Warn(warnings, $"unknown_race_color:{raca}")
                : null,
            MotherName = Clean(row.NomeMae),
            FatherName = Clean(row.NomePai),
            AdmissionDate = dates.StartOfDay(admission),
            EffectiveDate = dates.StartOfDay(effective),
            JobRoleExternalId = row.Cargo!.Trim(),
            WorkplaceExternalId = WorkplaceKey.For(row.Cdempresa, row.Cdfilial),
            Company = companyId,
            CostCenter = CostCenter(row),
            Intern = row.TipoColaborador == CodeMaps.TipoColaboradorEstagiario,
            TypeOfLaborRelationship = CodeMaps.LaborRelationship.GetValueOrDefault(row.TipoColaborador),
        };

        return new EmployeeMapping(payload, errors, warnings);
    }

    /// <summary>Primeiro dia em que o colaborador existe no DP (effectiveDate).</summary>
    public static DateOnly EffectiveDate(EmployeeRow row, DateOnly? goLiveDate)
    {
        var admission = DateOnly.FromDateTime(row.DataAdmissao ?? DateTime.MinValue);
        return goLiveDate is { } live && live > admission ? live : admission;
    }

    private static string? CostCenter(EmployeeRow row)
    {
        var code = Clean(row.CentroCusto);
        var description = Clean(row.CentroCustoDescricao);
        return (code, description) switch
        {
            (null, null) => null,
            (not null, null) => code,
            (null, not null) => description,
            _ when string.Equals(code, description, StringComparison.Ordinal) => code,
            _ => $"{code} - {description}",
        };
    }

    private static string? Phone(string? ddd, string? number)
    {
        var digits = Digits(number);
        if (digits is null)
        {
            return null;
        }

        var area = Digits(ddd);
        return area is not null && digits.Length <= 9 ? area + digits : digits;
    }

    private static string? Lookup(
        IReadOnlyDictionary<string, string> map, string? code, string field, List<string> warnings, string? ignore = null)
    {
        var key = Clean(code);
        if (key is null || string.Equals(key, ignore, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return map.TryGetValue(key, out var value) ? value : Warn(warnings, $"unknown_{field}:{key}");
    }

    private static string? Warn(List<string> warnings, string warning)
    {
        warnings.Add(warning);
        return null;
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }
}
