using IntegracaoSolidesDP.Worker.Pipeline.Steps;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Estado da execução de uma empresa (uma conta do Sólides DP): referências resolvidas no DP e itens do relatório.</summary>
public sealed class SyncContext(Guid runId, int cdempresa, bool dryRun, DateOnly today)
{
    private readonly List<RunItem> _items = [];

    public Guid RunId { get; } = runId;

    /// <summary>Empresa da execução: todo estado e toda chamada à API são da conta dela.</summary>
    public int Cdempresa { get; } = cdempresa;

    public bool DryRun { get; } = dryRun;
    public DateOnly Today { get; } = today;

    /// <summary>
    /// Há token para consultar o DP. Na execução real, sempre; no dry-run, só se a empresa tiver token
    /// (aí a simulação mostra quem seria vinculado pelo CPF em vez de criado, sem escrever nada).
    /// </summary>
    public bool CanQueryDp { get; init; } = !dryRun;

    /// <summary>Colaboradores ativos da conta no DP, por CPF e por Código Externo (carregado na primeira necessidade).</summary>
    public DpEmployeeIndex? DpEmployees { get; set; }

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

/// <summary>Empresa da execução e as filiais marcadas (vazio = todas).</summary>
public sealed record EscopoEmpresa(int Cdempresa, IReadOnlySet<int> Filiais)
{
    public bool Inclui(int cdfilial) => Filiais.Count == 0 || Filiais.Contains(cdfilial);
}

/// <summary>Colaboradores de uma empresa do RHSenso já separados por CPF em ativos, saídas e pendências.</summary>
public sealed record EmployeePlan(
    IReadOnlyList<EmployeeRow> Active,
    IReadOnlyList<Departure> Departures,
    IReadOnlyList<RunItem> Skipped)
{
    /// <summary>CPFs ativos e em escopo (as chaves do estado).</summary>
    public IReadOnlySet<string> ActiveKeys { get; } = Active.Select(r => r.Chave!).ToHashSet(StringComparer.Ordinal);

    /// <summary>Ativos numa filial que não está marcada: não são enviados nem desligados.</summary>
    public IReadOnlyDictionary<string, EmployeeRow> OutOfScope { get; init; } = new Dictionary<string, EmployeeRow>(StringComparer.Ordinal);

    /// <summary>Matrícula → CPF da empresa (para ligar as férias, que vêm por matrícula, ao colaborador).</summary>
    public IReadOnlyDictionary<string, string> KeyByMatricula { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>CPF → rótulo "{empresa}-{matrícula}" usado no relatório.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string LabelFor(string key) => Labels.TryGetValue(key, out var label) ? label : key;
}

/// <summary>
/// CPF sem vínculo ativo na empresa: desligamento (situação de demissão/aposentadoria) ou
/// transferência para outra empresa (todas as linhas 09). Só é aplicado a quem a integração já vinculou.
/// </summary>
public sealed record Departure(string Key, string Label, DateOnly Date, string Reason, bool IsTransfer);
