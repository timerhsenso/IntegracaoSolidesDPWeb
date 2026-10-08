using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Cargos (cargo1) usados por colaboradores em escopo. O cadastro de cargo do DP não tem
/// allowUpdate: antes de criar, procura pelo externalId; cargo já existente com descrição
/// diferente fica como aviso (não há endpoint de atualização).
/// </summary>
public sealed class JobRoleStep(ISolidesDpClient api, ISourceReader source, IStateStore state)
{
    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var needed = plan.Active
            .Select(r => EmployeeMapper.Clean(r.Cargo))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var rows = await source.ReadJobRolesAsync(needed, ct);
        var states = await state.LoadEntityStatesAsync(EntityTypes.JobRole, ct);

        foreach (var missing in needed.Except(rows.Select(r => r.Cdcargo), StringComparer.Ordinal))
        {
            context.Add(new RunItem(EntityTypes.JobRole, missing, ItemActions.None, ItemStatuses.Failed,
                Message: "cargo referenciado em func1 não existe em cargo1"));
        }

        foreach (var row in rows.OrderBy(r => r.Cdcargo, StringComparer.Ordinal))
        {
            var payload = JobRoleMapper.Map(row);
            if (payload is null)
            {
                context.Add(new RunItem(EntityTypes.JobRole, row.Cdcargo, ItemActions.None, ItemStatuses.Failed, Message: "cargo sem descrição"));
                continue;
            }

            var hash = PayloadHasher.Hash(payload);
            states.TryGetValue(row.Cdcargo, out var current);

            if (current?.RemoteId is not null && current.PayloadHash == hash)
            {
                context.AvailableJobRoles.Add(row.Cdcargo);
                context.Add(new RunItem(EntityTypes.JobRole, row.Cdcargo, ItemActions.None, ItemStatuses.Unchanged));
                continue;
            }

            if (context.DryRun)
            {
                context.AvailableJobRoles.Add(row.Cdcargo);
                context.Add(new RunItem(EntityTypes.JobRole, row.Cdcargo,
                    current?.RemoteId is null ? ItemActions.Create : ItemActions.Update, ItemStatuses.DryRun, Message: payload.Description));
                continue;
            }

            if (current?.RemoteId is { } remoteId)
            {
                // Sem endpoint de atualização: registra o novo hash para avisar uma vez só.
                await state.UpsertEntityStateAsync(current with { PayloadHash = hash, Status = EntityStatuses.Synced }, ct);
                context.AvailableJobRoles.Add(row.Cdcargo);
                context.Add(new RunItem(EntityTypes.JobRole, row.Cdcargo, ItemActions.Update, ItemStatuses.Warning,
                    Message: $"update_unsupported: descrição mudou para '{payload.Description}'; ajustar manualmente no DP (id {remoteId})"));
                continue;
            }

            await CreateAsync(context, row.Cdcargo, payload, hash, ct);
        }
    }

    private async Task CreateAsync(SyncContext context, string code, JobRoleRequest payload, string hash, CancellationToken ct)
    {
        // Também cobre um POST anterior cuja resposta se perdeu.
        var found = await api.FindJobRoleAsync(code, ct);
        ReferenceResolver.ThrowIfUnauthorized(found);
        if (found is { IsSuccess: true, Value.Id: > 0 })
        {
            await SaveAsync(code, found.Value.Id, hash, ct);
            context.AvailableJobRoles.Add(code);
            context.Add(new RunItem(EntityTypes.JobRole, code, ItemActions.Create, ItemStatuses.Adopted, found.HttpStatus,
                Message: $"já existia no DP (id {found.Value.Id})"));
            return;
        }

        if (found.Outcome != ApiOutcome.NotFound)
        {
            context.Add(new RunItem(EntityTypes.JobRole, code, ItemActions.Create, ItemStatuses.Failed, found.HttpStatus, found.Message));
            return;
        }

        var created = await api.RegisterJobRoleAsync(payload, ct);
        ReferenceResolver.ThrowIfUnauthorized(created);
        if (created is { IsSuccess: true, Value.Id: > 0 })
        {
            await SaveAsync(code, created.Value.Id, hash, ct);
            context.AvailableJobRoles.Add(code);
            context.Add(new RunItem(EntityTypes.JobRole, code, ItemActions.Create, ItemStatuses.Created, created.HttpStatus));
            return;
        }

        context.Add(new RunItem(EntityTypes.JobRole, code, ItemActions.Create, ItemStatuses.Failed, created.HttpStatus, created.Message));
    }

    private Task SaveAsync(string code, long remoteId, string hash, CancellationToken ct) =>
        state.UpsertEntityStateAsync(new EntityState
        {
            EntityType = EntityTypes.JobRole,
            ExternalId = code,
            RemoteId = remoteId,
            PayloadHash = hash,
            Status = EntityStatuses.Synced,
        }, ct);
}
