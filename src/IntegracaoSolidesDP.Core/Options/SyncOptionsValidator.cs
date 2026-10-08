using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>Regras da seção Sync. Usado no start do serviço, em cada execução com a gestão ligada e pela Web antes de gravar.</summary>
public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions options)
    {
        var errors = new List<string>();

        if (options.TiposColaborador.Count == 0)
        {
            errors.Add("Sync:TiposColaborador não pode ser vazio.");
        }

        if (!options.DryRun && options.GoLiveDate is null)
        {
            errors.Add("Sync:GoLiveDate é obrigatório fora do dry-run (vira o effectiveDate de quem já trabalha na empresa).");
        }

        if (options.FeriasJanelaDias is < 0 or > 3650)
        {
            errors.Add("Sync:FeriasJanelaDias deve estar entre 0 e 3650.");
        }

        if (options.MaxCreatesPerRun < 0 || options.MaxCancellationsPerRun < 0)
        {
            errors.Add("Sync:MaxCreatesPerRun e Sync:MaxCancellationsPerRun não podem ser negativos.");
        }

        if (options.FeriasMaxTentativas < 1)
        {
            errors.Add("Sync:FeriasMaxTentativas deve ser pelo menos 1.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceName) || options.InstanceName.Length > 64)
        {
            errors.Add("Sync:InstanceName deve ter entre 1 e 64 caracteres.");
        }

        foreach (var (code, reason) in options.MotivoDemissaoMap)
        {
            if (!Mapping.CodeMaps.ResignationReasons.Contains(reason))
            {
                errors.Add($"Sync:MotivoDemissaoMap:{code} = '{reason}' não é um resignationReason válido do DP.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
