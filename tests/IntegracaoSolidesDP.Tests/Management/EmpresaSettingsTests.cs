using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Tests.Management;

/// <summary>Regras gerais (todas as empresas) x regras da empresa (tela Empresas).</summary>
public sealed class EmpresaSettingsTests
{
    private static readonly SyncOptions Geral = new()
    {
        InstanceName = "adn",
        DryRun = true,
        GoLiveDate = new DateOnly(2026, 11, 1),
        WorkScheduleExternalId = "ESCALA-GERAL",
        PunchRuleExternalId = "REGRA-GERAL",
        CompanyMode = CompanyMode.None,
        CreateMissingCompanies = true,
        FeriasMotivoId = 99,
        TiposColaborador = [1, 2],
        EmpresasIncluidas = [1, 15],
        ExternalIdAllowList = ["GERAL"],
        MaxCreatesPerRun = 7,
    };

    [Fact]
    public void Account_rules_come_only_from_the_company()
    {
        var options = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao
        {
            Cdempresa = 15,
            DryRun = false,
            GoLiveDate = new DateOnly(2026, 12, 1),
            WorkScheduleExternalId = " ESCALA-15 ",
            FeriasMotivoId = 7,
            ModoEmpresa = ModosEmpresa.PorCnpj,
            CriarEmpresasFaltantes = false,
            Piloto = [" 15-00007811 ", "", "12345678901"],
        });

        options.EmpresasIncluidas.Should().Equal(15);
        options.DryRun.Should().BeFalse("a simulação é só da empresa");
        options.GoLiveDate.Should().Be(new DateOnly(2026, 12, 1));
        options.WorkScheduleExternalId.Should().Be("ESCALA-15");
        options.PunchRuleExternalId.Should().BeEmpty("vazio = padrão da conta, não a regra de outra conta");
        options.FeriasMotivoId.Should().Be(7);
        options.CompanyMode.Should().Be(CompanyMode.ResolveByCnpj);
        options.CreateMissingCompanies.Should().BeFalse();
        options.ExternalIdAllowList.Should().Equal("15-00007811", "12345678901");
    }

    [Fact]
    public void General_rules_are_shared_by_every_company()
    {
        var options = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 1 });

        options.InstanceName.Should().Be("adn");
        options.TiposColaborador.Should().Equal(1, 2);
        options.MaxCreatesPerRun.Should().Be(7);
    }

    [Fact]
    public void Empty_company_rules_do_not_inherit_the_old_general_fields()
    {
        var options = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 1, DryRun = false });

        options.GoLiveDate.Should().BeNull();
        options.WorkScheduleExternalId.Should().BeEmpty();
        options.FeriasMotivoId.Should().BeNull();
        options.CompanyMode.Should().Be(CompanyMode.ResolveByCnpj, "o padrão da empresa é procurar pelo CNPJ");
        options.CreateMissingCompanies.Should().BeFalse();
        options.ExternalIdAllowList.Should().BeEmpty("piloto vazio = todos");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dry_run_is_decided_by_the_company(bool empresa)
    {
        var general = EmpresaOptions.Copiar(Geral, dryRun: !empresa);

        SyncSettingsLoader.OptionsFor(general, new EmpresaConfiguracao { Cdempresa = 1, DryRun = empresa }).DryRun.Should().Be(empresa);
    }

    [Fact]
    public void Company_mode_none_sends_no_company()
    {
        SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 1, ModoEmpresa = ModosEmpresa.Nenhuma })
            .CompanyMode.Should().Be(CompanyMode.None);
    }

    [Fact]
    public void General_rules_are_not_modified()
    {
        _ = SyncSettingsLoader.OptionsFor(Geral, new EmpresaConfiguracao { Cdempresa = 15, GoLiveDate = new DateOnly(2027, 1, 1), Piloto = ["x"] });

        Geral.EmpresasIncluidas.Should().Equal(1, 15);
        Geral.GoLiveDate.Should().Be(new DateOnly(2026, 11, 1));
        Geral.ExternalIdAllowList.Should().Equal("GERAL");
    }
}
