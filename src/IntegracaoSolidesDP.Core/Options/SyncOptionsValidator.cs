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

        if (options.MaxCreatesPerRun < 0 || options.MaxCancellationsPerRun < 0 || options.MaxDismissalsPerRun < 0)
        {
            errors.Add("Sync:MaxCreatesPerRun, Sync:MaxCancellationsPerRun e Sync:MaxDismissalsPerRun não podem ser negativos.");
        }

        if (options.FeriasMaxTentativas < 1)
        {
            errors.Add("Sync:FeriasMaxTentativas deve ser pelo menos 1.");
        }

        if (options.ColaboradorMaxTentativas < 1)
        {
            errors.Add("Sync:ColaboradorMaxTentativas deve ser pelo menos 1.");
        }

        if (options.RetencaoExecucoesDias is not 0 and (< 30 or > 3650))
        {
            errors.Add("Sync:RetencaoExecucoesDias deve ser 0 (não apaga) ou de 30 a 3650.");
        }

        if (options.RetencaoArquivosDias is < 0 or > 3650)
        {
            errors.Add("Sync:RetencaoArquivosDias deve ser de 0 (não apaga) a 3650.");
        }

        if (options.LimpezaHora is < 0 or > 23)
        {
            errors.Add("Sync:LimpezaHora deve ser de 0 a 23.");
        }

        if (options.ConferenciaHora is < 0 or > 23)
        {
            errors.Add("Sync:ConferenciaHora deve ser de 0 a 23.");
        }

        if (!Enum.IsDefined(options.ConferenciaDiaSemana))
        {
            errors.Add("Sync:ConferenciaDiaSemana inválido.");
        }

        if (!Enum.IsDefined(options.AgendaModo))
        {
            errors.Add("Sync:AgendaModo inválido.");
        }

        if (options.AgendaModo != AgendaModo.ArquivoDoServico)
        {
            if (options.AgendaDiasSemana.Count == 0 || options.AgendaDiasSemana.Any(d => !Enum.IsDefined(d)))
            {
                errors.Add("Sync:AgendaDiasSemana: marque pelo menos um dia da semana.");
            }

            if (options.AgendaModo == AgendaModo.Intervalo && options.AgendaIntervaloMinutos is < 5 or > 1440)
            {
                errors.Add("Sync:AgendaIntervaloMinutos deve ser de 5 a 1440 minutos.");
            }

            if (options.AgendaModo == AgendaModo.Horarios)
            {
                if (options.AgendaHorarios.Count == 0)
                {
                    errors.Add("Sync:AgendaHorarios: informe pelo menos um horário.");
                }

                foreach (var horario in options.AgendaHorarios.Where(h => !HorarioAgenda.TentarLer(h, out _)))
                {
                    errors.Add($"Sync:AgendaHorarios: \"{horario}\" não é um horário (use HH:mm, ex.: 08:00).");
                }
            }
        }

        if (options.ServicoSemRespostaMinutos is < 1 or > 1440)
        {
            errors.Add("Sync:ServicoSemRespostaMinutos deve ser de 1 a 1440.");
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
