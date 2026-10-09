using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Regras efetivas de uma empresa, usadas pelo serviço e pela Web. Das gerais (seção Sync) vêm só as regras da folha:
/// tipos de colaborador, situações, motivos de demissão, férias e travas. O resto é da empresa (conta do Sólides DP e
/// fase da implantação): simulação, go-live, piloto, escala, regra de ponto, motivo FÉRIAS e empresa no Sólides DP.
/// </summary>
public static class EmpresaOptions
{
    public static SyncOptions Mesclar(SyncOptions geral, EmpresaConfiguracao empresa)
    {
        ArgumentNullException.ThrowIfNull(geral);
        ArgumentNullException.ThrowIfNull(empresa);

        var options = Copiar(geral, empresa.DryRun);
        options.EmpresasIncluidas = [empresa.Cdempresa];
        options.ExternalIdAllowList = empresa.Piloto.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        options.GoLiveDate = empresa.GoLiveDate;
        options.WorkScheduleExternalId = Valor(empresa.WorkScheduleExternalId) ?? string.Empty;
        options.PunchRuleExternalId = Valor(empresa.PunchRuleExternalId) ?? string.Empty;
        options.FeriasMotivoId = empresa.FeriasMotivoId;
        options.CompanyMode = empresa.ModoEmpresa == ModosEmpresa.Nenhuma ? CompanyMode.None : CompanyMode.ResolveByCnpj;
        options.CreateMissingCompanies = empresa.CriarEmpresasFaltantes;
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
