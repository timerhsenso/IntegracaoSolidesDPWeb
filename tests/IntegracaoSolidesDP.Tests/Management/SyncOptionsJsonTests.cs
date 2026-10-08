using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Tests.Management;

public sealed class SyncOptionsJsonTests
{
    [Fact]
    public void Round_trip_keeps_every_rule()
    {
        var original = new SyncOptions
        {
            InstanceName = "adn",
            DryRun = false,
            TiposColaborador = [1],
            EmpresasIncluidas = [7, 14],
            ExternalIdAllowList = ["14-00901482"],
            SituacoesIgnoradas = ["99", "98"],
            GoLiveDate = new DateOnly(2026, 11, 1),
            WorkScheduleExternalId = "ESC-01",
            CompanyMode = CompanyMode.None,
            AllowDoubleBind = true,
            MotivoDemissaoMap = new Dictionary<string, string> { ["21"] = "DEMISSAO" },
            FeriasEnviarProgramadas = true,
            FeriasMotivoId = 42,
            FeriasEndDateMode = FeriasEndDateMode.FimDoUltimoDia,
            MaxCreatesPerRun = 50,
        };

        var copy = SyncOptionsJson.Deserialize(SyncOptionsJson.Serialize(original));

        copy.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Json_uses_the_appsettings_key_names_and_enum_names()
    {
        var json = SyncOptionsJson.Serialize(new SyncOptions { FeriasEndDateMode = FeriasEndDateMode.InicioDoUltimoDia });

        json.Should().Contain("\"FeriasJanelaDias\"").And.Contain("\"InicioDoUltimoDia\"").And.Contain("\"ResolveByCnpj\"");
    }

    [Fact]
    public void Lists_in_the_json_replace_the_defaults_instead_of_appending()
    {
        var options = SyncOptionsJson.Deserialize("""{ "TiposColaborador": [2] }""");

        options.TiposColaborador.Should().Equal(2);
        options.DryRun.Should().BeTrue("chaves ausentes ficam com o padrão seguro");
    }
}
