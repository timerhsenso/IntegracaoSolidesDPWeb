using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Worker.State;
using SolidesDP.Fake.Configuration;

namespace IntegracaoSolidesDP.Tests.EndToEnd;

[Collection(SqlServerCollection.Name)]
public sealed class VacationEndToEndTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly RhuSeed _seed = new(db);
    private E2EHarness _harness = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _seed.BasicsAsync();
        await _seed.FuncionarioAsync(matric: "00000001");
        _harness = new E2EHarness(db);
        await _harness.Fake.ResetAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Changing_the_dates_updates_the_same_adjustment()
    {
        var feria = await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 2);
        await _harness.RunAsync(Ct);
        var created = (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Single();

        await db.ExecuteAsync("UPDATE dbo.feria2 SET dtinipf = '2026-11-02', dtfimpf = '2026-11-13' WHERE id = @feria", new { feria });
        var summary = await _harness.RunAsync(Ct);

        summary.Status.Should().Be(RunStatuses.Completed, summary.Error);
        var updated = (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Should().ContainSingle().Subject;
        updated.Id.Should().Be(created.Id);
        updated.StartDate.Should().Be(TestData.Dates.StartOfDay(new DateOnly(2026, 11, 2)));
        updated.EndDate.Should().Be(TestData.Dates.StartOfDay(new DateOnly(2026, 11, 14)));
    }

    [Fact]
    public async Task Rescheduled_vacation_is_excluded_in_dp()
    {
        var feria = await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 6);
        await _harness.RunAsync(Ct);

        await db.ExecuteAsync("UPDATE dbo.feria2 SET flconfirm = 7 WHERE id = @feria", new { feria });
        var summary = await _harness.RunAsync(Ct);

        summary.Counts["vacation"]["cancelled"].Should().Be(1);
        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Single().Excluded.Should().BeTrue();
    }

    [Fact]
    public async Task Vacation_deleted_in_rhsenso_is_excluded_in_dp()
    {
        var feria = await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 6);
        await _harness.RunAsync(Ct);

        await db.ExecuteAsync("DELETE FROM dbo.feria2 WHERE id = @feria", new { feria });
        await _harness.RunAsync(Ct);

        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Single().Excluded.Should().BeTrue();
    }

    [Fact]
    public async Task Vacation_out_of_the_window_is_not_mistaken_for_deleted()
    {
        var feria = await _seed.FeriasAsync("00000001", new DateTime(2026, 9, 1), new DateTime(2026, 9, 15), situacao: 6);
        await _harness.RunAsync(Ct);

        _harness.Clock.Advance(TimeSpan.FromDays(120));
        await _harness.Fake.ClearRequestsAsync(Ct);
        await _harness.RunAsync(Ct);

        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Single().Excluded.Should().BeFalse();
        (await _harness.WritesAsync(Ct)).Should().NotContain(w => w.Path.StartsWith("/adjustment", StringComparison.Ordinal));
        _ = feria;
    }

    [Fact]
    public async Task Lost_response_is_reconciled_instead_of_creating_a_duplicate()
    {
        await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 2);
        await _harness.Fake.AddFaultAsync(new FaultRule { Method = "POST", Path = "/adjustment/register/1.1", Kind = FaultKind.CommitThenDrop, Times = 1 }, Ct);

        var first = await _harness.RunAsync(Ct);
        var second = await _harness.RunAsync(Ct);

        first.Counts["vacation"]["failed"].Should().Be(1, "o resultado do POST ficou desconhecido");
        second.Status.Should().Be(RunStatuses.Completed, second.Error);
        second.Counts["vacation"].Should().ContainKey("adopted");
        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Should().ContainSingle("o lançamento não pode ser duplicado");
    }

    [Fact]
    public async Task Scheduled_vacation_is_not_sent_until_released()
    {
        var feria = await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 1);
        await _harness.RunAsync(Ct);
        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Should().BeEmpty();

        await db.ExecuteAsync("UPDATE dbo.feria2 SET flconfirm = 2 WHERE id = @feria", new { feria });
        await _harness.RunAsync(Ct);

        (await _harness.Fake.GetStateAsync(Ct)).Adjustments.Should().ContainSingle().Which.Status.Should().Be("APROVADO");
    }

    [Fact]
    public async Task Rejection_by_dp_is_retried_on_later_runs_then_given_up()
    {
        await _seed.FeriasAsync("00000001", new DateTime(2026, 10, 19), new DateTime(2026, 10, 30), situacao: 2);
        _harness.Settings["Sync:FeriasMaxTentativas"] = "2";
        await _harness.Fake.AddFaultAsync(new FaultRule { Method = "POST", Path = "/adjustment/register/1.1", Kind = FaultKind.ErrorInBody, Times = 10, Message = "Período fechado" }, Ct);

        await _harness.RunAsync(Ct);
        await _harness.RunAsync(Ct);
        await _harness.Fake.ClearRequestsAsync(Ct);
        var third = await _harness.RunAsync(Ct);

        third.Counts["vacation"]["skipped"].Should().Be(1);
        (await _harness.WritesAsync(Ct)).Should().BeEmpty();
    }
}
