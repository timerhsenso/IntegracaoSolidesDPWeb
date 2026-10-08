using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Desligamentos e transferências para outra empresa. Roda antes dos colaboradores para que
/// o vínculo novo (mesmo CPF, outra empresa) não colida com o antigo ainda ativo no DP.
/// Só toca quem a integração criou: os desligados históricos nunca são enviados.
/// </summary>
public sealed class DismissalStep(ISolidesDpClient api, IStateStore state, EpochDates dates)
{
    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(EntityTypes.Employee, ct);

        foreach (var departure in plan.Departures.OrderBy(d => d.Date))
        {
            if (!states.TryGetValue(departure.ExternalId, out var current)
                || current.RemoteId is null
                || current.Status == EntityStatuses.Dismissed)
            {
                continue;
            }

            var label = departure.IsTransfer ? $"transferência ({departure.Reason})" : departure.Reason;
            if (departure.Date > context.Today)
            {
                context.Add(new RunItem(EntityTypes.Employee, departure.ExternalId, ItemActions.Dismiss, ItemStatuses.Deferred,
                    Message: $"desligamento em {departure.Date:yyyy-MM-dd}: {label}"));
                continue;
            }

            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, departure.ExternalId, ItemActions.Dismiss, ItemStatuses.DryRun,
                    Message: $"{departure.Date:yyyy-MM-dd}: {label}"));
                continue;
            }

            var result = await api.DismissEmployeeAsync(
                new DismissRequest(departure.ExternalId, dates.StartOfDay(departure.Date), departure.Reason), ct);
            ReferenceResolver.ThrowIfUnauthorized(result);

            if (result.IsSuccess)
            {
                await state.UpsertEntityStateAsync(current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, departure.ExternalId, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"{departure.Date:yyyy-MM-dd}: {label}"));
                continue;
            }

            // Um retry pode ter desligado e a resposta se perdido: confere antes de reportar falha.
            var check = await api.FindEmployeeAsync(departure.ExternalId, ct);
            if (check is { IsSuccess: true, Value.Fired: true })
            {
                await state.UpsertEntityStateAsync(current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, departure.ExternalId, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"já estava desligado no DP ({result.Message})"));
                continue;
            }

            context.Add(new RunItem(EntityTypes.Employee, departure.ExternalId, ItemActions.Dismiss, ItemStatuses.Failed,
                result.HttpStatus, result.Message));
        }
    }
}
