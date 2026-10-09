using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Regras efetivas de uma empresa: as gerais (seção Sync) com as da empresa por cima. Usado pelo serviço e pela Web.</summary>
public static class EmpresaOptions
{
    /// <summary>A simulação vale se estiver ligada na regra geral ou na empresa. Campos vazios da empresa usam o geral.</summary>
    public static SyncOptions Mesclar(SyncOptions geral, EmpresaConfiguracao empresa)
    {
        ArgumentNullException.ThrowIfNull(geral);
        ArgumentNullException.ThrowIfNull(empresa);

        var options = Copiar(geral, geral.DryRun || empresa.DryRun);
        options.EmpresasIncluidas = [empresa.Cdempresa];
        options.GoLiveDate = empresa.GoLiveDate ?? geral.GoLiveDate;
        options.WorkScheduleExternalId = Valor(empresa.WorkScheduleExternalId) ?? geral.WorkScheduleExternalId;
        options.PunchRuleExternalId = Valor(empresa.PunchRuleExternalId) ?? geral.PunchRuleExternalId;
        options.FeriasMotivoId = empresa.FeriasMotivoId ?? geral.FeriasMotivoId;
        options.CompanyMode = empresa.ModoEmpresa switch
        {
            ModosEmpresa.Nenhuma => CompanyMode.None,
            ModosEmpresa.PorCnpj => CompanyMode.ResolveByCnpj,
            _ => geral.CompanyMode,
        };
        return options;
    }

    /// <summary>Cópia independente (as listas não são compartilhadas), com o modo simulação informado.</summary>
    public static SyncOptions Copiar(SyncOptions source, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = SyncOptionsJson.Deserialize(SyncOptionsJson.Serialize(source));
        copy.InstanceName = source.InstanceName;
        copy.ReportDirectory = source.ReportDirectory;
        copy.DryRun = dryRun;
        return copy;
    }

    private static string? Valor(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
