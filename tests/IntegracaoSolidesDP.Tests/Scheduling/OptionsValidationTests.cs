using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Hosting.Internal;

namespace IntegracaoSolidesDP.Tests.Scheduling;

public sealed class OptionsValidationTests
{
    [Fact]
    public void Exactly_one_of_interval_or_times_of_day()
    {
        var validator = new ExecutionOptionsValidator();

        validator.Validate(null, new ExecutionOptions()).Failed.Should().BeTrue();
        validator.Validate(null, new ExecutionOptions { Interval = TimeSpan.FromMinutes(30), TimesOfDay = [TimeSpan.FromHours(7)] }).Failed.Should().BeTrue();
        validator.Validate(null, new ExecutionOptions { Interval = TimeSpan.FromMinutes(30) }).Succeeded.Should().BeTrue();
        validator.Validate(null, new ExecutionOptions { TimesOfDay = [TimeSpan.FromHours(7)] }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Unknown_time_zone_is_rejected()
    {
        new ExecutionOptionsValidator()
            .Validate(null, new ExecutionOptions { Interval = TimeSpan.FromHours(1), TimeZone = "Mars/Olympus" })
            .Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, false)]
    [InlineData("Production", false, false)]
    public void Real_api_is_refused_outside_production_without_explicit_opt_in(string environment, bool allow, bool shouldFail)
    {
        var validator = new SolidesDpOptionsValidator(
            new HostingEnvironment { EnvironmentName = environment },
            Microsoft.Extensions.Options.Options.Create(new SyncOptions { DryRun = true }));

        var result = validator.Validate(null, new SolidesDpOptions
        {
            BaseUrl = "https://employer.tangerino.com.br",
            AllowProductionApiOutsideProduction = allow,
        });

        result.Failed.Should().Be(shouldFail);
    }

    [Fact]
    public void Real_runs_need_a_token_and_a_go_live_date()
    {
        var sync = new SyncOptions { DryRun = false };
        var api = new SolidesDpOptionsValidator(new HostingEnvironment { EnvironmentName = "Production" }, Microsoft.Extensions.Options.Options.Create(sync));

        api.Validate(null, new SolidesDpOptions { Token = "" }).Failed.Should().BeTrue();
        new SyncOptionsValidator().Validate(null, sync).Failed.Should().BeTrue();
        new SyncOptionsValidator().Validate(null, new SyncOptions { DryRun = false, GoLiveDate = new DateOnly(2026, 11, 1) }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Configured_resignation_reasons_must_exist_in_the_supplier_enum()
    {
        var options = new SyncOptions { MotivoDemissaoMap = new Dictionary<string, string> { ["11"] = "DEMITIDO_SEM_MOTIVO" } };

        new SyncOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }
}
