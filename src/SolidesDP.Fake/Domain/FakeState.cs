namespace SolidesDP.Fake.Domain;

/// <summary>
/// Estado completo do fake: e o corpo de <c>GET /_fake/state</c> e tambem o formato do arquivo de seed.
/// </summary>
public sealed record FakeState
{
    public IReadOnlyList<CompanyRecord> Companies { get; init; } = [];

    public IReadOnlyList<JobRoleRecord> JobRoles { get; init; } = [];

    public IReadOnlyList<WorkplaceRecord> Workplaces { get; init; } = [];

    public IReadOnlyList<WorkScheduleRecord> WorkSchedules { get; init; } = [];

    public IReadOnlyList<PunchRuleRecord> PunchRules { get; init; } = [];

    public IReadOnlyList<AdjustmentReasonRecord> AdjustmentReasons { get; init; } = [];

    public IReadOnlyList<EmployeeRecord> Employees { get; init; } = [];

    public IReadOnlyList<AdjustmentRecord> Adjustments { get; init; } = [];
}
