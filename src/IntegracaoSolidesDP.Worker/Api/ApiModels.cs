using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntegracaoSolidesDP.Worker.Api;

// Contratos da API do Sólides DP usados pelo worker. Os nomes seguem as definitions do
// Swagger (spec/tangerino-employer.json); o teste de contrato garante que todo campo
// enviado existe lá. Datas são epoch milissegundos.

public static class SolidesDpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>EmployeeDTO (POST /employee/register).</summary>
public sealed record EmployeeRequest
{
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
    public string? Matricula { get; init; }
    public string? Cpf { get; init; }
    public string? Pis { get; init; }
    public string? Ctps { get; init; }
    public string? Series { get; init; }
    public long? BirthDate { get; init; }
    public string? Email { get; init; }
    public string? CorporateEmail { get; init; }
    public string? PersonalEmail { get; init; }
    public string? Phone { get; init; }
    public string? Gender { get; init; }
    public string? MaritalStatus { get; init; }
    public string? EducationLevel { get; init; }
    public string? RaceColor { get; init; }
    public string? MotherName { get; init; }
    public string? FatherName { get; init; }
    public long? AdmissionDate { get; init; }
    public long? EffectiveDate { get; init; }
    public string? JobRoleExternalId { get; init; }
    public string? WorkplaceExternalId { get; init; }
    public long? Company { get; init; }
    public string? CostCenter { get; init; }
    public bool? Intern { get; init; }
    public string? TypeOfLaborRelationship { get; init; }
    public bool? DoubleBindEmployee { get; init; }
    public long? WorkSchedule { get; init; }
    public string? WorkScheduleExternalId { get; init; }
    public long? WorkScheduleDateInMillis { get; init; }
    public string? PunchRuleExternalId { get; init; }
    public long? PunchRuleDateInMillis { get; init; }
}

/// <summary>JobRoleDTO (POST /job-role/register).</summary>
public sealed record JobRoleRequest(string Description, string ExternalId, string? Cbo);

/// <summary>WorkplaceDTO (POST /workplace/register).</summary>
public sealed record WorkplaceRequest(string Name, string ExternalId);

/// <summary>DismissDTO (POST /employee/dismiss).</summary>
public sealed record DismissRequest(string ExternalId, long ResignationDate, string ResignationReason);

/// <summary>CompanyCNPJDTO (POST /companies).</summary>
public sealed record CompanyRequest(string Cnpj, string? FantasyName, string? SocialReason);

/// <summary>AdjustmentReasonRegisterDTO (POST /adjustment/register/1.1).</summary>
public sealed record AdjustmentRegisterRequest
{
    public long AdjustmentReasonId { get; init; }
    public string EmployeeExternalId { get; init; } = string.Empty;
    public long StartDate { get; init; }
    public long EndDate { get; init; }
    public bool FullDay { get; init; } = true;
    public string? Origem { get; init; }
    public string? Observation { get; init; }
    public string? Status { get; init; }
}

/// <summary>AdjustmentReasonRecordUpdateV2DTO (PUT /adjustment/update/{id}). O nome com "adjustmente" é o do Swagger.</summary>
public sealed record AdjustmentUpdateRequest
{
    public long AdjustmentReasonId { get; init; }
    public long AdjustmenteReasonRecordId { get; init; }
    public long EmployeeId { get; init; }
    public long StartDate { get; init; }
    public long EndDate { get; init; }
    public bool FullDay { get; init; } = true;
    public string? Origem { get; init; }
    public string? Observation { get; init; }
    public string? Status { get; init; }
    public bool? Excluded { get; init; }
}

// ---- Respostas: só os campos usados. Campos extras são ignorados na leitura. ----

public sealed record PageDto<T>
{
    public IReadOnlyList<T> Content { get; init; } = [];
    public bool? Last { get; init; }
    public int? Number { get; init; }
    public int? TotalPages { get; init; }
}

public sealed record BaseItemDto<T>
{
    public T? Item { get; init; }
}

public sealed record CompanyDto
{
    public long Id { get; init; }
    public string? Cnpj { get; init; }
    public string? ExternalId { get; init; }
    public string? FantasyName { get; init; }
    public string? SocialReason { get; init; }
}

public sealed record WorkScheduleDto
{
    public long Id { get; init; }
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
    public bool? Standard { get; init; }
}

public sealed record PunchRuleDto
{
    public long Id { get; init; }
    public string? ExternalId { get; init; }
    public string? Description { get; init; }
    public bool? Standard { get; init; }
}

public sealed record AdjustmentReasonDto
{
    public long Id { get; init; }
    public string? Description { get; init; }
    public bool? Active { get; init; }
}

public sealed record JobRoleDto
{
    public long Id { get; init; }
    public string? ExternalId { get; init; }
    public string? Description { get; init; }
}

public sealed record WorkplaceDto
{
    public long Id { get; init; }
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
}

public sealed record EmployeeDto
{
    public long Id { get; init; }
    public string? ExternalId { get; init; }
    public string? Name { get; init; }
    public bool? Fired { get; init; }
    public WorkScheduleDto? CurrentWorkSchedule { get; init; }
}

public sealed record AdjustmentRecordDto
{
    public long Id { get; init; }
    public long? StartDate { get; init; }
    public long? EndDate { get; init; }
    public string? Observation { get; init; }
    public string? Status { get; init; }
}
