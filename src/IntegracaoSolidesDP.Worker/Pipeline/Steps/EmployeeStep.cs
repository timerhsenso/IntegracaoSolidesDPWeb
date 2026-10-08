using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Colaboradores ativos. Upsert por externalId (POST /employee/register?allowUpdate=true).
/// Escala e regra de ponto são obrigatórias no DTO: na criação vão os padrões do config; na
/// atualização reenviamos o que já está no DP, para não desfazer ajustes feitos pelo RH lá.
/// </summary>
public sealed class EmployeeStep(
    ISolidesDpClient api,
    IStateStore state,
    EmployeeMapper mapper,
    EpochDates dates,
    IOptions<SyncOptions> syncOptions)
{
    private SyncOptions Options => syncOptions.Value;

    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(EntityTypes.Employee, ct);
        var candidates = new List<(EmployeeRow Row, EmployeeRequest Payload, string Hash, string? Warnings, EntityState? Current)>();

        foreach (var row in plan.Active.OrderBy(r => r.Cdempresa).ThenBy(r => r.Nomatric, StringComparer.Ordinal))
        {
            var key = row.ExternalId;
            states.TryGetValue(key, out var current);

            long? companyId = null;
            if (Options.CompanyMode == CompanyMode.ResolveByCnpj && !context.DryRun)
            {
                var cnpj = EmployeeMapper.Digits(row.Cnpj);
                if (cnpj is null || !context.CompanyIdsByCnpj.TryGetValue(cnpj, out var id))
                {
                    context.Add(new RunItem(EntityTypes.Employee, key, ItemActions.None, ItemStatuses.Blocked,
                        Message: $"company_not_found: CNPJ '{cnpj ?? "vazio"}' da filial {row.Cdempresa}-{row.Cdfilial}"));
                    continue;
                }

                companyId = id;
            }

            var mapping = mapper.Map(row, Options.GoLiveDate, companyId);
            if (!mapping.IsValid)
            {
                context.Add(new RunItem(EntityTypes.Employee, key, ItemActions.None, ItemStatuses.Skipped,
                    Message: string.Join("; ", mapping.Errors.Concat(mapping.Warnings))));
                continue;
            }

            var payload = plan.DoubleBind.Contains(key) ? mapping.Payload! with { DoubleBindEmployee = true } : mapping.Payload!;
            var blockers = new List<string>();
            if (!context.AvailableJobRoles.Contains(payload.JobRoleExternalId!))
            {
                blockers.Add($"cargo {payload.JobRoleExternalId} indisponível no DP");
            }

            if (!context.AvailableWorkplaces.Contains(payload.WorkplaceExternalId!))
            {
                blockers.Add($"local {payload.WorkplaceExternalId} indisponível no DP");
            }

            if (blockers.Count > 0)
            {
                context.Add(new RunItem(EntityTypes.Employee, key, ItemActions.None, ItemStatuses.Blocked, Message: string.Join("; ", blockers)));
                continue;
            }

            var warnings = mapping.Warnings.Count == 0 ? null : string.Join("; ", mapping.Warnings);
            candidates.Add((row, payload, PayloadHasher.Hash(payload), warnings, current));
        }

        var creates = candidates.Count(c => c.Current?.RemoteId is null);
        if (!context.DryRun && creates > Options.MaxCreatesPerRun)
        {
            context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.Create, ItemStatuses.Failed,
                Message: $"max_creates_exceeded: {creates} colaboradores novos > Sync:MaxCreatesPerRun ({Options.MaxCreatesPerRun}). Nada foi enviado."));
            return;
        }

        foreach (var (row, payload, hash, warnings, current) in candidates)
        {
            var key = row.ExternalId;
            var unchanged = current is { RemoteId: not null, Status: EntityStatuses.Synced } && current.PayloadHash == hash;
            if (unchanged)
            {
                context.Add(new RunItem(EntityTypes.Employee, key, ItemActions.None, ItemStatuses.Unchanged));
                continue;
            }

            var action = current?.RemoteId is null ? ItemActions.Create : ItemActions.Update;
            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, key, action, ItemStatuses.DryRun, Message: Describe(payload, warnings)));
                continue;
            }

            await UpsertAsync(context, row, payload, hash, warnings, current, ct);
        }
    }

    private async Task UpsertAsync(
        SyncContext context, EmployeeRow row, EmployeeRequest payload, string hash, string? warnings, EntityState? current, CancellationToken ct)
    {
        var key = row.ExternalId;
        var existing = await api.FindEmployeeAsync(key, ct);
        ReferenceResolver.ThrowIfUnauthorized(existing);
        if (existing.Outcome is not (ApiOutcome.Success or ApiOutcome.NotFound))
        {
            context.Add(new RunItem(EntityTypes.Employee, key, ItemActions.Update, ItemStatuses.Failed, existing.HttpStatus, existing.Message));
            return;
        }

        var remote = existing.IsSuccess ? existing.Value : null;
        var schedule = ScheduleFor(context, row, remote, current);
        var request = payload with
        {
            WorkSchedule = schedule.WorkScheduleId,
            WorkScheduleDateInMillis = schedule.WorkScheduleDate,
            PunchRuleExternalId = schedule.PunchRuleExternalId,
            PunchRuleDateInMillis = schedule.PunchRuleDate,
        };

        var result = await api.RegisterEmployeeAsync(request, ct);
        ReferenceResolver.ThrowIfUnauthorized(result);
        var action = remote is null ? ItemActions.Create : ItemActions.Update;

        if (!result.IsSuccess)
        {
            var message = remote is { Fired: true }
                ? $"readmissão: colaborador está desligado no DP. {result.Message}"
                : result.Message;
            context.Add(new RunItem(EntityTypes.Employee, key, action, ItemStatuses.Failed, result.HttpStatus, message));
            return;
        }

        var remoteId = result.Value is { Id: > 0 } v ? v.Id : remote?.Id;
        await state.UpsertEntityStateAsync(new EntityState
        {
            EntityType = EntityTypes.Employee,
            ExternalId = key,
            RemoteId = remoteId,
            PayloadHash = hash,
            Status = EntityStatuses.Synced,
            ExtraJson = JsonSerializer.Serialize(schedule, SolidesDpJson.Options),
        }, ct);

        var status = (remote, current?.RemoteId) switch
        {
            (null, _) => ItemStatuses.Created,
            (not null, null) => ItemStatuses.Adopted,
            _ => ItemStatuses.Updated,
        };
        context.Add(new RunItem(EntityTypes.Employee, key, action, status, result.HttpStatus, warnings));
    }

    /// <summary>Escala/regra enviadas: padrões na criação; na atualização, a escala atual do DP e as datas da criação.</summary>
    private ScheduleAssignment ScheduleFor(SyncContext context, EmployeeRow row, EmployeeDto? remote, EntityState? current)
    {
        var effective = dates.StartOfDay(EmployeeMapper.EffectiveDate(row, Options.GoLiveDate));
        var stored = current?.ExtraJson is { } json ? JsonSerializer.Deserialize<ScheduleAssignment>(json, SolidesDpJson.Options) : null;

        if (remote is null)
        {
            return new ScheduleAssignment(context.DefaultWorkScheduleId, effective, context.DefaultPunchRuleExternalId, effective);
        }

        return new ScheduleAssignment(
            remote.CurrentWorkSchedule?.Id ?? stored?.WorkScheduleId ?? context.DefaultWorkScheduleId,
            stored?.WorkScheduleDate ?? effective,
            stored?.PunchRuleExternalId ?? context.DefaultPunchRuleExternalId,
            stored?.PunchRuleDate ?? effective);
    }

    private static string Describe(EmployeeRequest payload, string? warnings)
    {
        var text = $"{payload.Name} | cargo {payload.JobRoleExternalId} | local {payload.WorkplaceExternalId}";
        return warnings is null ? text : $"{text} | {warnings}";
    }
}

/// <summary>Escala e regra de ponto enviadas ao DP (guardadas no estado para as atualizações).</summary>
public sealed record ScheduleAssignment(long? WorkScheduleId, long WorkScheduleDate, string? PunchRuleExternalId, long PunchRuleDate);
