namespace IntegracaoSolidesDP.Worker.State;

public static class EntityTypes
{
    public const string JobRole = "job_role";
    public const string Workplace = "workplace";
    public const string Employee = "employee";
    public const string Company = "company";
    public const string Vacation = "vacation";
}

public static class EntityStatuses
{
    public const string Synced = "synced";
    public const string Failed = "failed";
    public const string Dismissed = "dismissed";
}

public static class VacationStatuses
{
    /// <summary>POST enviado sem resposta conhecida: reconciliar pelo marcador antes de reenviar.</summary>
    public const string Pending = "pending";
    public const string Synced = "synced";
    public const string Failed = "failed";

    /// <summary>Esgotou Sync:FeriasMaxTentativas; só volta a tentar se o período mudar.</summary>
    public const string FailedPermanent = "failed_permanent";
    public const string Cancelled = "cancelled";
}

/// <summary>O que já existe no DP para um cadastro (cargo, local, colaborador, empresa).</summary>
public sealed record EntityState
{
    public required string EntityType { get; init; }
    public required string ExternalId { get; init; }
    public long? RemoteId { get; init; }
    public string? PayloadHash { get; init; }
    public required string Status { get; init; }

    /// <summary>JSON livre por tipo (ex.: escala e regra de ponto enviadas na criação do colaborador).</summary>
    public string? ExtraJson { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>O que já existe no DP para um período de férias (feria2.id).</summary>
public sealed record VacationState
{
    public required Guid Feria2Id { get; init; }
    public required string EmployeeExternalId { get; init; }
    public long? RemoteAdjustmentId { get; init; }
    public string? PayloadHash { get; init; }
    public required string Status { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    /// <summary>Período enviado (epoch ms), para poder cancelar mesmo se a linha sumir do RHSenso.</summary>
    public long? StartDate { get; init; }
    public long? EndDate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Uma linha do relatório de execução.</summary>
public sealed record RunItem(
    string EntityType,
    string ExternalId,
    string Action,
    string Status,
    int? HttpStatus = null,
    string? Message = null);

public static class RunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
}
