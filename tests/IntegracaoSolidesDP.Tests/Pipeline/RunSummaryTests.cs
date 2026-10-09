using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Pipeline;

public sealed class RunSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static RunSummary Part(int empresa, string status, string? error = null, int created = 0) =>
        new(Guid.NewGuid(), status, false, Now, Now,
            created == 0
                ? new Dictionary<string, IReadOnlyDictionary<string, int>>()
                : new Dictionary<string, IReadOnlyDictionary<string, int>> { ["employee"] = new Dictionary<string, int> { ["created"] = created } },
            error, $"run-{empresa}.csv", empresa);

    [Fact]
    public void A_single_company_is_returned_as_is()
    {
        var only = Part(15, RunStatuses.Completed);

        RunSummary.Combine([only], false, Now, Now).Should().BeSameAs(only);
    }

    [Fact]
    public void Several_companies_add_up_counts_and_keep_the_parts()
    {
        var combined = RunSummary.Combine([Part(1, RunStatuses.Completed, created: 2), Part(15, RunStatuses.Completed, created: 3)], false, Now, Now);

        combined.Status.Should().Be(RunStatuses.Completed);
        combined.RunId.Should().Be(Guid.Empty);
        combined.Counts["employee"]["created"].Should().Be(5);
        combined.Empresas.Should().HaveCount(2);
        combined.ReportPath.Should().Be("run-1.csv | run-15.csv");
    }

    [Fact]
    public void One_failed_company_makes_the_whole_request_completed_with_errors()
    {
        var combined = RunSummary.Combine([Part(1, RunStatuses.Completed), Part(15, RunStatuses.Failed, "config_invalid: sem token")], false, Now, Now);

        combined.Status.Should().Be(RunStatuses.CompletedWithErrors);
        combined.Error.Should().Be("empresa 15: config_invalid: sem token");
    }

    [Fact]
    public void All_failed_is_failed_and_no_company_is_skipped()
    {
        RunSummary.Combine([Part(1, RunStatuses.Failed, "x"), Part(2, RunStatuses.Failed, "y")], false, Now, Now).Status.Should().Be(RunStatuses.Failed);
        RunSummary.Combine([], false, Now, Now).Status.Should().Be(RunStatuses.SkippedDisabled);
    }
}
