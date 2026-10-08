using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Tests.Scheduling;

public sealed class ConfigurationBindingTests
{
    private static SyncOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddIntegracaoSolidesDP(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SyncOptions>>().Value;
    }

    [Fact]
    public void Configured_lists_replace_the_defaults_instead_of_appending()
    {
        var options = Bind(new() { ["Sync:TiposColaborador:0"] = "1", ["Sync:SituacoesIgnoradas:0"] = "98" });

        options.TiposColaborador.Should().Equal(1);
        options.SituacoesIgnoradas.Should().Equal("98");
    }

    [Fact]
    public void Defaults_apply_when_the_list_is_not_configured()
    {
        var options = Bind([]);

        options.TiposColaborador.Should().Equal(1, 2);
        options.SituacoesIgnoradas.Should().Equal("99");
    }
}
