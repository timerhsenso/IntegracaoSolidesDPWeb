using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Desligamentos e transferências para outra empresa, sempre por um fato do RHSenso (situação de demissão
/// ou aposentadoria, ou todas as linhas 09). Roda antes dos colaboradores. Só toca quem tem vínculo na conta
/// desta empresa (criado ou vinculado pelo CPF): os desligados históricos nunca são enviados, e mudança de
/// configuração (filial desmarcada, empresa inativa) nunca desliga ninguém.
/// </summary>
public sealed class DismissalStep(ISolidesDpClient api, IStateStore state, EpochDates dates)
{
    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(context.Cdempresa, EntityTypes.Employee, ct);

        foreach (var departure in plan.Departures.OrderBy(d => d.Date))
        {
            if (!states.TryGetValue(departure.Key, out var current)
                || current.RemoteId is not { } remoteId
                || current.Status == EntityStatuses.Dismissed)
            {
                continue;
            }

            var label = departure.Label;
            var reason = departure.IsTransfer ? $"transferência ({departure.Reason})" : departure.Reason;
            if (departure.Date > context.Today)
            {
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Deferred,
                    Message: $"desligamento em {departure.Date:yyyy-MM-dd}: {reason}"));
                continue;
            }

            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.DryRun,
                    Message: $"{departure.Date:yyyy-MM-dd}: {reason} (cadastro {remoteId} do DP)"));
                continue;
            }

            var result = await api.DismissEmployeeAsync(
                new DismissRequest(remoteId, dates.StartOfDay(departure.Date), departure.Reason), ct);
            ReferenceResolver.ThrowIfUnauthorized(result);

            if (result.IsSuccess)
            {
                await state.UpsertEntityStateAsync(context.Cdempresa, current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"{departure.Date:yyyy-MM-dd}: {reason}"));
                continue;
            }

            // Um retry pode ter desligado e a resposta se perdido: confere antes de reportar falha.
            var check = await api.FindEmployeeByIdAsync(remoteId, ct);
            if (check is { IsSuccess: true, Value.Fired: true })
            {
                await state.UpsertEntityStateAsync(context.Cdempresa, current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"já estava desligado no DP ({result.Message})"));
                continue;
            }

            context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Failed,
                result.HttpStatus, result.Message));
        }
    }
}
