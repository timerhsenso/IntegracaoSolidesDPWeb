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

/// <summary>
/// O que já existe no DP para um cadastro de uma empresa (conta). <see cref="ExternalId"/> é a chave na integração:
/// o CPF do colaborador, o código do cargo ou "{empresa}-{filial}" do local de trabalho.
/// </summary>
public sealed record EntityState
{
    public required string EntityType { get; init; }
    public required string ExternalId { get; init; }
    public long? RemoteId { get; init; }
    public string? PayloadHash { get; init; }
    public required string Status { get; init; }

    /// <summary>JSON livre por tipo (ex.: escala e regra de ponto enviadas na criação do colaborador).</summary>
    public string? ExtraJson { get; init; }

    /// <summary>Colaborador: o "Código Externo" que está no Sólides DP (pode não ser a matrícula; ver a regra no EmployeeStep).</summary>
    public string? CodigoExterno { get; init; }

    /// <summary>Colaborador: matrícula no RHSenso.</summary>
    public string? Matricula { get; init; }

    /// <summary>Colaborador: filial atual; local de trabalho: a própria filial.</summary>
    public int? Cdfilial { get; init; }

    /// <summary>Colaborador: "criado" pela integração ou "vinculado_cpf" (já existia no DP).</summary>
    public string? Origem { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Valores de <see cref="EntityState.Origem"/>.</summary>
public static class OrigensVinculo
{
    public const string Criado = "criado";
    public const string VinculadoCpf = "vinculado_cpf";
}

/// <summary>O que já existe no DP para um período de férias (feria2.id).</summary>
public sealed record VacationState
{
    public required Guid Feria2Id { get; init; }

    /// <summary>CPF do colaborador (chave em solidesdp.colaborador_vinculo).</summary>
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

    /// <summary>Não executou: outra execução da instância estava em andamento (não grava em runs).</summary>
    public const string SkippedLocked = "skipped_locked";

    /// <summary>Não executou: integração desativada na Web (não grava em runs).</summary>
    public const string SkippedDisabled = "skipped_disabled";
}
