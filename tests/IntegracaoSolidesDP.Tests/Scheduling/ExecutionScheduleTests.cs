using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Scheduling;

namespace IntegracaoSolidesDP.Tests.Scheduling;

public sealed class ExecutionScheduleTests
{
    private static DateTimeOffset Bahia(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Interval_runs_on_startup_and_then_every_interval()
    {
        var schedule = new ExecutionSchedule(new ExecutionOptions { Interval = TimeSpan.FromMinutes(30) }, TestData.Bahia);
        var now = Bahia(2026, 10, 5, 10, 0);

        schedule.RunOnStartup.Should().BeTrue();
        schedule.Next(now).Should().Be(now.AddMinutes(30));
    }

    [Fact]
    public void Times_of_day_are_local_to_the_configured_zone_not_utc()
    {
        var schedule = new ExecutionSchedule(
            new ExecutionOptions { TimesOfDay = [TimeSpan.FromHours(13), TimeSpan.FromHours(7)] }, TestData.Bahia);

        schedule.RunOnStartup.Should().BeFalse();
        schedule.Next(Bahia(2026, 10, 5, 6, 59)).Should().Be(Bahia(2026, 10, 5, 7, 0));
        schedule.Next(Bahia(2026, 10, 5, 7, 0)).Should().Be(Bahia(2026, 10, 5, 13, 0));
    }

    [Fact]
    public void After_the_last_time_of_day_the_next_run_is_tomorrow()
    {
        var schedule = new ExecutionSchedule(new ExecutionOptions { TimesOfDay = [TimeSpan.FromHours(7), TimeSpan.FromHours(23.5)] }, TestData.Bahia);

        schedule.Next(Bahia(2026, 12, 31, 23, 45)).Should().Be(Bahia(2027, 1, 1, 7, 0));
    }
}
