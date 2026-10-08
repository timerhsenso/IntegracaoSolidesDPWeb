using SolidesDP.Fake.Domain;

namespace SolidesDP.Fake.Store;

/// <summary>Tipos de entidade com contador de id proprio.</summary>
internal enum EntityKind
{
    Company,
    JobRole,
    Workplace,
    WorkSchedule,
    PunchRule,
    AdjustmentReason,
    Employee,
    Adjustment,
}

/// <summary>
/// Dados mutaveis do fake. So deve ser tocado dentro de <see cref="FakeStore.Execute{T}"/> (que segura o lock unico).
/// </summary>
internal sealed class StoreData
{
    /// <summary>Primeiro id gerado para entidades novas (os ids do seed ficam abaixo disso).</summary>
    public const long FirstGeneratedId = 10000;

    private readonly Dictionary<EntityKind, long> _lastIds = [];

    public SortedDictionary<long, CompanyRecord> Companies { get; } = [];

    public SortedDictionary<long, JobRoleRecord> JobRoles { get; } = [];

    public SortedDictionary<long, WorkplaceRecord> Workplaces { get; } = [];

    public SortedDictionary<long, WorkScheduleRecord> WorkSchedules { get; } = [];

    public SortedDictionary<long, PunchRuleRecord> PunchRules { get; } = [];

    public SortedDictionary<long, AdjustmentReasonRecord> AdjustmentReasons { get; } = [];

    public SortedDictionary<long, EmployeeRecord> Employees { get; } = [];

    public SortedDictionary<long, AdjustmentRecord> Adjustments { get; } = [];

    public static StoreData From(FakeState state)
    {
        var data = new StoreData();
        Load(data.Companies, state.Companies, r => r.Id);
        Load(data.JobRoles, state.JobRoles, r => r.Id);
        Load(data.Workplaces, state.Workplaces, r => r.Id);
        Load(data.WorkSchedules, state.WorkSchedules, r => r.Id);
        Load(data.PunchRules, state.PunchRules, r => r.Id);
        Load(data.AdjustmentReasons, state.AdjustmentReasons, r => r.Id);
        Load(data.Employees, state.Employees, r => r.Id);
        Load(data.Adjustments, state.Adjustments, r => r.Id);

        data.SeedCounter(EntityKind.Company, data.Companies.Keys);
        data.SeedCounter(EntityKind.JobRole, data.JobRoles.Keys);
        data.SeedCounter(EntityKind.Workplace, data.Workplaces.Keys);
        data.SeedCounter(EntityKind.WorkSchedule, data.WorkSchedules.Keys);
        data.SeedCounter(EntityKind.PunchRule, data.PunchRules.Keys);
        data.SeedCounter(EntityKind.AdjustmentReason, data.AdjustmentReasons.Keys);
        data.SeedCounter(EntityKind.Employee, data.Employees.Keys);
        data.SeedCounter(EntityKind.Adjustment, data.Adjustments.Keys);
        return data;
    }

    /// <summary>Gera o proximo id (auto-increment por tipo de entidade, a partir de 10000).</summary>
    public long NextId(EntityKind kind)
    {
        var next = _lastIds.GetValueOrDefault(kind, FirstGeneratedId - 1) + 1;
        _lastIds[kind] = next;
        return next;
    }

    public FakeState ToState() => new()
    {
        Companies = [.. Companies.Values],
        JobRoles = [.. JobRoles.Values],
        Workplaces = [.. Workplaces.Values],
        WorkSchedules = [.. WorkSchedules.Values],
        PunchRules = [.. PunchRules.Values],
        AdjustmentReasons = [.. AdjustmentReasons.Values],
        Employees = [.. Employees.Values],
        Adjustments = [.. Adjustments.Values],
    };

    private static void Load<T>(SortedDictionary<long, T> target, IEnumerable<T> source, Func<T, long> id)
    {
        foreach (var item in source)
        {
            target[id(item)] = item;
        }
    }

    private void SeedCounter(EntityKind kind, IEnumerable<long> existingIds)
    {
        var max = existingIds.DefaultIfEmpty(0).Max();
        _lastIds[kind] = Math.Max(FirstGeneratedId - 1, max);
    }
}
