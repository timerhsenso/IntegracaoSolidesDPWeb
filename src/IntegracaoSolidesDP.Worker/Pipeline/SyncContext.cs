using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Estado de uma execução: referências resolvidas no DP e itens do relatório.</summary>
public sealed class SyncContext(Guid runId, bool dryRun, DateOnly today)
{
    private readonly List<RunItem> _items = [];

    public Guid RunId { get; } = runId;
    public bool DryRun { get; } = dryRun;
    public DateOnly Today { get; } = today;

    public IReadOnlyList<RunItem> Items => _items;

    /// <summary>Algum item falhou ou foi bloqueado: a execução termina como completed_with_errors.</summary>
    public bool HasErrors { get; private set; }

    /// <summary>CNPJ (só dígitos) → id da empresa no DP.</summary>
    public Dictionary<string, long> CompanyIdsByCnpj { get; } = new(StringComparer.Ordinal);

    /// <summary>Cargos (externalId) que existem no DP nesta execução.</summary>
    public HashSet<string> AvailableJobRoles { get; } = new(StringComparer.Ordinal);

    /// <summary>Locais (externalId) que existem no DP nesta execução.</summary>
    public HashSet<string> AvailableWorkplaces { get; } = new(StringComparer.Ordinal);

    public long? DefaultWorkScheduleId { get; set; }
    public string? DefaultPunchRuleExternalId { get; set; }
    public long? FeriasReasonId { get; set; }

    public void Add(RunItem item)
    {
        _items.Add(item);
        if (item.Status is ItemStatuses.Failed or ItemStatuses.Blocked)
        {
            HasErrors = true;
        }
    }

    public void AddRange(IEnumerable<RunItem> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void MarkError() => HasErrors = true;
}

public static class ItemStatuses
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Adopted = "adopted";
    public const string Unchanged = "unchanged";
    public const string Dismissed = "dismissed";
    public const string Cancelled = "cancelled";
    public const string DryRun = "dry_run";
    public const string Skipped = "skipped";
    public const string Deferred = "deferred";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Warning = "warning";
}

public static class ItemActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Dismiss = "dismiss";
    public const string Cancel = "cancel";
    public const string Resolve = "resolve";
    public const string None = "none";
}

/// <summary>Erro que interrompe a execução inteira (token inválido, fonte vazia, trava de segurança).</summary>
public sealed class SyncAbortedException(string message) : Exception(message);

/// <summary>Colaboradores do RHSenso já separados em ativos e saídas.</summary>
public sealed record EmployeePlan(
    IReadOnlyList<EmployeeRow> Active,
    IReadOnlyList<Departure> Departures,
    IReadOnlySet<string> DoubleBind,
    IReadOnlyList<RunItem> Skipped)
{
    public IReadOnlySet<string> ActiveKeys { get; } = Active.Select(r => r.ExternalId).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Colaborador sem linha ativa na sua chave: desligamento (08) ou transferência para outra empresa (09).</summary>
public sealed record Departure(string ExternalId, DateOnly Date, string Reason, bool IsTransfer);
