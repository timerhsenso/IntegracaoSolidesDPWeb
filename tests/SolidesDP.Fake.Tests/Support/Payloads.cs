
namespace SolidesDP.Fake.Tests.Support;

/// <summary>Construtores de payloads validos segundo o Swagger.</summary>
public static class Payloads
{
    /// <summary>2024-01-01T00:00:00Z em epoch ms.</summary>
    public const long Jan2024 = 1_704_067_200_000;

    public const long Day = 86_400_000;

    public static JsonObject Employee(string externalId, string? cpf = null) => new()
    {
        ["name"] = "COLABORADOR " + externalId,
        ["externalId"] = externalId,
        ["cpf"] = cpf,
        ["admissionDate"] = Jan2024,
        ["effectiveDate"] = Jan2024,
        ["workScheduleDateInMillis"] = Jan2024,
        ["punchRuleDateInMillis"] = Jan2024,
    };

    /// <summary>Exemplo "Lancamento de ferias" da documentacao publica do fornecedor.</summary>
    public static JsonObject Vacation(string employeeExternalId, long start, long end) => new()
    {
        ["adjustmentReasonDTO"] = new JsonObject { ["id"] = 1 },
        ["employeeDTO"] = new JsonObject { ["externalId"] = employeeExternalId },
        ["startDate"] = start,
        ["endDate"] = end,
        ["fullDay"] = true,
        ["origem"] = "Integração",
        ["status"] = "APROVADO",
    };

    /// <summary>Corpo do <c>POST /adjustment/register/1.1</c>.</summary>
    public static JsonObject Vacation11(string employeeExternalId, long start, long end) => new()
    {
        ["adjustmentReasonId"] = 1,
        ["employeeExternalId"] = employeeExternalId,
        ["startDate"] = start,
        ["endDate"] = end,
        ["fullDay"] = true,
        ["origem"] = "Integração",
        ["status"] = "APROVADO",
    };

    public static JsonObject Dismiss(string externalId, string? reason = "DEMISSAO_EMPRESA") => new()
    {
        ["externalId"] = externalId,
        ["resignationDate"] = Jan2024 + (30 * Day),
        ["resignationReason"] = reason,
    };
}
