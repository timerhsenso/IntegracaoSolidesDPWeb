using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Tests.Management;

public sealed class EmpresaSettingsTests
{
    private static readonly SyncOptions Geral = new()
    {
        InstanceName = "adn",
        DryRun = false,
        GoLiveDate = new DateOnly(2026, 11, 1),
        WorkScheduleExternalId = "ESCALA-GERAL",
        PunchRuleExternalId = "REGRA-GERAL",
        CompanyMode = CompanyMode.ResolveByCnpj,
        TiposColaborador = [1, 2],
        EmpresasIncluidas = [1, 15],
    };

    [Fact]
    public void Company_rules_override_the_general_ones()
    {
        var options = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao
        {
            Cdempresa = 15,
            DryRun = false,
            GoLiveDate = new DateOnly(2026, 12, 1),
            WorkScheduleExternalId = " ESCALA-15 ",
            FeriasMotivoId = 7,
            ModoEmpresa = ModosEmpresa.Nenhuma,
        });

        options.EmpresasIncluidas.Should().Equal(15);
        options.DryRun.Should().BeFalse();
        options.GoLiveDate.Should().Be(new DateOnly(2026, 12, 1));
        options.WorkScheduleExternalId.Should().Be("ESCALA-15");
        options.PunchRuleExternalId.Should().Be("REGRA-GERAL");
        options.FeriasMotivoId.Should().Be(7);
        options.CompanyMode.Should().Be(CompanyMode.None);
        options.InstanceName.Should().Be("adn");
        options.TiposColaborador.Should().Equal(1, 2);
    }

    [Fact]
    public void Empty_company_rules_keep_the_general_ones()
    {
        var options = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 1, DryRun = false });

        options.GoLiveDate.Should().Be(Geral.GoLiveDate);
        options.WorkScheduleExternalId.Should().Be("ESCALA-GERAL");
        options.CompanyMode.Should().Be(CompanyMode.ResolveByCnpj);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Dry_run_wins_when_either_side_asks_for_it(bool geral, bool empresa, bool esperado)
    {
        var general = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 1 });
        general.DryRun = geral;

        SyncSettingsLoader.OptionsFor(general, new EmpresaConfiguracao { Cdempresa = 1, DryRun = empresa }).DryRun.Should().Be(esperado);
    }

    [Fact]
    public void General_rules_are_not_modified()
    {
        _ = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 15, GoLiveDate = new DateOnly(2027, 1, 1) });

        Geral.EmpresasIncluidas.Should().Equal(1, 15);
        Geral.GoLiveDate.Should().Be(new DateOnly(2026, 11, 1));
    }
}
