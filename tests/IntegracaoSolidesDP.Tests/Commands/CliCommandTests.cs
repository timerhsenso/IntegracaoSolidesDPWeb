using IntegracaoSolidesDP.Worker.Commands;

namespace IntegracaoSolidesDP.Tests.Commands;

public sealed class CliCommandTests
{
    [Theory]
    [InlineData("--dry-run --empresa 15")]
    [InlineData("--empresa=15 --dry-run")]
    public void Company_option_is_parsed_and_not_passed_to_the_configuration(string args)
    {
        var command = CliCommand.Parse(args.Split(' '));

        command.Mode.Should().Be(CliMode.DryRun);
        command.Empresa.Should().Be(15);
        command.HostArgs.Should().NotContain(a => a.Contains("empresa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Without_the_option_every_enabled_company_runs()
    {
        CliCommand.Parse(["--run-once"]).Empresa.Should().BeNull();
    }

    [Theory]
    [InlineData("--empresa")]
    [InlineData("--empresa abc")]
    [InlineData("--empresa=0")]
    public void Invalid_company_is_refused(string args)
    {
        var parse = () => CliCommand.Parse(args.Split(' '));

        parse.Should().Throw<ArgumentException>().WithMessage("*--empresa*");
    }
}
