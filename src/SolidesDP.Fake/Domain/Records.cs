using System.Text.Json.Nodes;

namespace SolidesDP.Fake.Domain;

/// <summary>Filial (<c>CompanyCNPJDTO</c>). O JSON do registro e exatamente o da resposta do Swagger.</summary>
public sealed record CompanyRecord
{
    public long Id { get; init; }

    public string Cnpj { get; init; } = "";

    public string? CnpjMask { get; init; }

    public string? DescriptionName { get; init; }

    public string? ExternalId { get; init; }

    public string? FantasyName { get; init; }

    public string? SocialReason { get; init; }
}

/// <summary>Cargo (<c>JobRoleDTO</c>). O JSON do registro e exatamente o da resposta do Swagger.</summary>
public sealed record JobRoleRecord
{
    public long Id { get; init; }

    public string Description { get; init; } = "";

    public string? ExternalId { get; init; }

    public string? Cbo { get; init; }

    /// <summary>Ultima alteracao (epoch ms).</summary>
    public long AlterationDate { get; init; }
}

/// <summary>Local de trabalho. <see cref="UpdatedAt"/> e interno (nao faz parte de <c>WorkplaceReturnDTO</c>).</summary>
public sealed record WorkplaceRecord
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public string? ExternalId { get; init; }

    public bool Active { get; init; } = true;

    public bool Standard { get; init; }

    public long UpdatedAt { get; init; }
}

/// <summary>Linha da grade de uma escala (<c>WorkScheduleTimetableDTO</c>); horarios em ms desde a meia-noite.</summary>
public sealed record WorkScheduleTimetableRecord
{
    public long Id { get; init; }

    /// <summary>Dia da semana (1 = domingo ... 7 = sabado, convencao <c>java.util.Calendar</c>).</summary>
    public int Day { get; init; }

    public long? StartShift1 { get; init; }

    public long? EndShift1 { get; init; }

    public long? StartShift2 { get; init; }

    public long? EndShift2 { get; init; }

    public long? StartMainInterval { get; init; }

    public long? EndMainInterval { get; init; }

    public string? MainInterval { get; init; }
}

/// <summary>Escala de trabalho (<c>WorkScheduleReturnDTO</c>). O JSON do registro e exatamente o da resposta.</summary>
public sealed record WorkScheduleRecord
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public string? ExternalId { get; init; }

    public bool Standard { get; init; }

    public bool Inactive { get; init; }

    public bool IgnoreHoliday { get; init; }

    public bool PreAssignedInterval { get; init; }

    public bool ShowIntradayInTimeSheet { get; init; }

    public long AlterationDate { get; init; }

    public IReadOnlyList<WorkScheduleTimetableRecord> WorkScheduleTimetableList { get; init; } = [];
}

/// <summary>Regra de ponto (subconjunto de <c>PunchRuleV2ResponseDTO</c>). O JSON do registro e o da resposta.</summary>
public sealed record PunchRuleRecord
{
    public long Id { get; init; }

    public string Description { get; init; } = "";

    public string? ExternalId { get; init; }

    public bool Standard { get; init; }

    public long RegistrationDate { get; init; }

    public int? ClosingDayTimeSheet { get; init; }

    public int? ToleranceMinutes { get; init; }

    public string? ToleranceType { get; init; }

    public string? ScheduleControlType { get; init; }

    public string? HoursBalanceControlType { get; init; }

    public string? OvertimeScope { get; init; }

    public bool? ExtendedNightTime { get; init; }

    public bool? FaultsNegativeTimeBank { get; init; }
}

/// <summary>Motivo de lancamento (<c>AdjustmentReasonResponseDTO</c>). O JSON do registro e o da resposta.</summary>
public sealed record AdjustmentReasonRecord
{
    public long Id { get; init; }

    public string Description { get; init; } = "";

    public bool Active { get; init; } = true;

    public bool Allowance { get; init; }

    public bool FullDay { get; init; }

    public bool CountAsMissing { get; init; }

    public bool AccountAsAbsenteeism { get; init; }

    public bool EnabledForEmployees { get; init; } = true;

    public bool EnabledForManagers { get; init; } = true;

    public bool LimitRecord { get; init; }
}

/// <summary>Entrada do historico de escalas de um colaborador.</summary>
public sealed record WorkScheduleHistoryEntry
{
    public long WorkScheduleId { get; init; }

    /// <summary>Data de vigencia informada em <c>workScheduleDateInMillis</c> (epoch ms).</summary>
    public long DateInMillis { get; init; }

    /// <summary>Quando o fake registrou a entrada (epoch ms).</summary>
    public long RegisteredAt { get; init; }
}

/// <summary>
/// Colaborador (registro interno completo). As propriedades do <c>EmployeeDTO</c> sem tratamento especial ficam em
/// <see cref="Fields"/> exatamente como foram aceitas.
/// </summary>
public sealed record EmployeeRecord
{
    public long Id { get; init; }

    public string? ExternalId { get; init; }

    public string Name { get; init; } = "";

    public string? Cpf { get; init; }

    public string? Pis { get; init; }

    public bool DoubleBindEmployee { get; init; }

    public bool Fired { get; init; }

    /// <summary>Data da demissao (epoch ms).</summary>
    public long? ResignationDate { get; init; }

    /// <summary>Motivo da demissao (<c>resignationReason</c>; exposto como <c>motivoDemissao</c>).</summary>
    public string? ResignationReason { get; init; }

    public long? CompanyId { get; init; }

    public long? WorkplaceId { get; init; }

    public long? JobRoleId { get; init; }

    public long WorkScheduleId { get; init; }

    public long? WorkScheduleDateInMillis { get; init; }

    public long PunchRuleId { get; init; }

    public long? PunchRuleDateInMillis { get; init; }

    public IReadOnlyList<WorkScheduleHistoryEntry> WorkScheduleHistory { get; init; } = [];

    /// <summary>Cabecalho <c>gestorId</c> do ultimo register (vinculo com gestor, nao modelado).</summary>
    public long? LinkedManagerId { get; init; }

    public string Pin { get; init; } = "";

    public long CreatedAt { get; init; }

    public long UpdatedAt { get; init; }

    /// <summary>Demais propriedades do <c>EmployeeDTO</c> aceitas (matricula, email, birthDate, employeeSalary, ...).</summary>
    public JsonObject Fields { get; init; } = [];
}

/// <summary>Lancamento (ferias, abono, atestado...). <see cref="Excluded"/> e o "soft delete" do update V2.</summary>
public sealed record AdjustmentRecord
{
    public long Id { get; init; }

    public long EmployeeId { get; init; }

    public long AdjustmentReasonId { get; init; }

    public long StartDate { get; init; }

    public long EndDate { get; init; }

    public bool FullDay { get; init; }

    public string Status { get; init; } = "APROVADO";

    public string? Observation { get; init; }

    public string? Origem { get; init; }

    public bool FirstDayIsPartial { get; init; }

    public bool Excluded { get; init; }

    public bool Edited { get; init; }

    public long CreatedAt { get; init; }

    public long LastUpdate { get; init; }
}
