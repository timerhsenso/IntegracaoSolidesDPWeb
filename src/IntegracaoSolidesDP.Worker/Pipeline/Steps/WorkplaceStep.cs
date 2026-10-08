using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>Locais de trabalho = filiais (test1) com colaboradores em escopo. Upsert por externalId (allowUpdate=true).</summary>
public sealed class WorkplaceStep(ISolidesDpClient api, ISourceReader source, IStateStore state)
{
    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var needed = plan.Active
            .Select(r => WorkplaceKey.For(r.Cdempresa, r.Cdfilial))
            .ToHashSet(StringComparer.Ordinal);
        var rows = (await source.ReadWorkplacesAsync(ct)).Where(w => needed.Contains(w.ExternalId)).ToList();
        var states = await state.LoadEntityStatesAsync(EntityTypes.Workplace, ct);

        foreach (var missing in needed.Except(rows.Select(r => r.ExternalId), StringComparer.Ordinal))
        {
            context.Add(new RunItem(EntityTypes.Workplace, missing, ItemActions.None, ItemStatuses.Failed,
                Message: "filial referenciada em func1 não existe em test1"));
        }

        foreach (var row in rows.OrderBy(r => r.Cdempresa).ThenBy(r => r.Cdfilial))
        {
            var payload = WorkplaceMapper.Map(row);
            var hash = PayloadHasher.Hash(payload);
            states.TryGetValue(row.ExternalId, out var current);

            if (current?.RemoteId is not null && current.PayloadHash == hash)
            {
                context.AvailableWorkplaces.Add(row.ExternalId);
                context.Add(new RunItem(EntityTypes.Workplace, row.ExternalId, ItemActions.None, ItemStatuses.Unchanged));
                continue;
            }

            var action = current?.RemoteId is null ? ItemActions.Create : ItemActions.Update;
            if (context.DryRun)
            {
                context.AvailableWorkplaces.Add(row.ExternalId);
                context.Add(new RunItem(EntityTypes.Workplace, row.ExternalId, action, ItemStatuses.DryRun, Message: payload.Name));
                continue;
            }

            var result = await api.RegisterWorkplaceAsync(payload, ct);
            ReferenceResolver.ThrowIfUnauthorized(result);
            if (result is { IsSuccess: true, Value.Id: > 0 })
            {
                await state.UpsertEntityStateAsync(new EntityState
                {
                    EntityType = EntityTypes.Workplace,
                    ExternalId = row.ExternalId,
                    RemoteId = result.Value.Id,
                    PayloadHash = hash,
                    Status = EntityStatuses.Synced,
                }, ct);
                context.AvailableWorkplaces.Add(row.ExternalId);
                context.Add(new RunItem(EntityTypes.Workplace, row.ExternalId, action,
                    action == ItemActions.Create ? ItemStatuses.Created : ItemStatuses.Updated, result.HttpStatus));
                continue;
            }

            if (current?.RemoteId is not null)
            {
                // Já existe no DP; a falha foi só na atualização do nome. Colaboradores podem referenciá-lo.
                context.AvailableWorkplaces.Add(row.ExternalId);
            }

            context.Add(new RunItem(EntityTypes.Workplace, row.ExternalId, action, ItemStatuses.Failed, result.HttpStatus, result.Message));
        }
    }
}
