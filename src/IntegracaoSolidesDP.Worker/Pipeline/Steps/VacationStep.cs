using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Férias (feria2) → ajuste de ponto com motivo FÉRIAS. Uma parcela = um lançamento.
/// O POST de criação não tem chave idempotente, então: grava "pending" antes de enviar,
/// não usa retry automático e, se o resultado ficar desconhecido, reconcilia na próxima
/// execução procurando o marcador "RHSenso:{feria2.id}" em observation.
/// </summary>
public sealed class VacationStep(
    ISolidesDpClient api,
    ISourceReader source,
    IStateStore state,
    VacationMapper mapper,
    IOptions<SyncOptions> syncOptions)
{
    private SyncOptions Options => syncOptions.Value;

    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        if (!context.DryRun && context.FeriasReasonId is null)
        {
            // O ReferenceResolver já registrou o motivo no relatório.
            return;
        }

        var reasonId = context.FeriasReasonId ?? 0;
        var desde = context.Today.AddDays(-Options.FeriasJanelaDias);
        var rows = await source.ReadVacationsEndingFromAsync(desde, ct);
        var employees = await state.LoadEntityStatesAsync(EntityTypes.Employee, ct);
        var vacations = await state.LoadVacationStatesAsync(ct);
        var allowList = Options.ExternalIdAllowList.Count == 0 ? null : Options.ExternalIdAllowList.ToHashSet(StringComparer.Ordinal);
        var cancellations = new List<(VacationState State, string Reason)>();

        foreach (var row in rows.OrderBy(r => r.Inicio))
        {
            var key = row.EmployeeExternalId;
            if (allowList is not null && !allowList.Contains(key))
            {
                continue;
            }

            employees.TryGetValue(key, out var employee);
            vacations.TryGetValue(row.Id, out var current);
            var itemKey = $"{key}:{row.Id:D}";

            if (Options.GoLiveDate is { } goLive && DateOnly.FromDateTime(row.Fim) < goLive)
            {
                continue;
            }

            var mapping = mapper.Map(row);
            if (mapping.Decision == VacationDecision.Cancel)
            {
                if (current is { RemoteAdjustmentId: not null } && current.Status != VacationStatuses.Cancelled)
                {
                    cancellations.Add((current, "reprogramada no RHSenso"));
                }

                continue;
            }

            if (mapping.Decision == VacationDecision.Skip)
            {
                if (plan.ActiveKeys.Contains(key))
                {
                    var reason = mapping.SkipReason == "programada"
                        ? $"programada {row.Inicio:yyyy-MM-dd}..{row.Fim:yyyy-MM-dd} (enviada quando for liberada)"
                        : mapping.SkipReason;
                    context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.None, ItemStatuses.Skipped, Message: reason));
                }

                continue;
            }

            var employeeReady = employee is { RemoteId: not null } && employee.Status != EntityStatuses.Dismissed;
            if (!employeeReady && !(context.DryRun && plan.ActiveKeys.Contains(key)))
            {
                if (plan.ActiveKeys.Contains(key))
                {
                    context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Create, ItemStatuses.Deferred,
                        Message: "colaborador ainda não sincronizado com o DP"));
                }

                continue;
            }

            var hash = mapping.Hash(reasonId, key);
            if (current is { Status: VacationStatuses.Synced } && current.PayloadHash == hash)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.None, ItemStatuses.Unchanged));
                continue;
            }

            if (current is { Status: VacationStatuses.FailedPermanent } && current.PayloadHash == hash)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.None, ItemStatuses.Skipped,
                    Message: $"desistiu após {current.Attempts} tentativas: {current.LastError}"));
                continue;
            }

            var warnings = mapping.Warnings.Count == 0 ? null : string.Join("; ", mapping.Warnings);
            var period = $"{row.Inicio:yyyy-MM-dd}..{row.Fim:yyyy-MM-dd} {mapping.Status}";
            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey,
                    current?.RemoteAdjustmentId is null ? ItemActions.Create : ItemActions.Update, ItemStatuses.DryRun,
                    Message: warnings is null ? period : $"{period} | {warnings}"));
                continue;
            }

            await SendAsync(context, row, mapping, hash, reasonId, employee!.RemoteId!.Value, current, itemKey, warnings, ct);
        }

        await CollectDeletedAsync(rows, vacations, cancellations, ct);
        await CancelAsync(context, reasonId, employees, cancellations, ct);
    }

    private async Task SendAsync(
        SyncContext context, VacationRow row, VacationMapping mapping, string hash, long reasonId, long employeeId,
        VacationState? current, string itemKey, string? warnings, CancellationToken ct)
    {
        var baseState = current ?? new VacationState
        {
            Feria2Id = row.Id,
            EmployeeExternalId = row.EmployeeExternalId,
            Status = VacationStatuses.Pending,
        };
        var attempts = current?.PayloadHash == hash ? current.Attempts : 0;

        // Resposta perdida numa execução anterior: o lançamento pode existir no DP.
        if (current is { Status: VacationStatuses.Pending, RemoteAdjustmentId: null })
        {
            var existing = await api.FindAdjustmentsAsync(employeeId, reasonId, ct);
            ReferenceResolver.ThrowIfUnauthorized(existing);
            if (!existing.IsSuccess)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Create, ItemStatuses.Failed, existing.HttpStatus,
                    $"não foi possível reconciliar envio anterior: {existing.Message}"));
                return;
            }

            var marker = VacationMapper.Marker(row.Id);
            var match = existing.Value!.FirstOrDefault(a => string.Equals(a.Observation, marker, StringComparison.Ordinal));
            if (match is not null)
            {
                baseState = baseState with { RemoteAdjustmentId = match.Id, Status = VacationStatuses.Synced };
                if (match.StartDate == mapping.StartDate && match.EndDate == mapping.EndDate && match.Status == mapping.Status)
                {
                    await state.UpsertVacationStateAsync(baseState with
                    {
                        PayloadHash = hash, Attempts = 0, LastError = null, StartDate = mapping.StartDate, EndDate = mapping.EndDate,
                    }, ct);
                    context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Create, ItemStatuses.Adopted,
                        Message: $"envio anterior confirmado no DP (id {match.Id})"));
                    return;
                }
            }
        }

        if (baseState.RemoteAdjustmentId is { } remoteId)
        {
            var updated = await api.UpdateAdjustmentAsync(remoteId, UpdateRequest(row.Id, remoteId, employeeId, reasonId, mapping, excluded: null), ct);
            ReferenceResolver.ThrowIfUnauthorized(updated);
            await RecordAsync(context, baseState, updated, hash, attempts, mapping, ItemActions.Update, itemKey, warnings, ct);
            return;
        }

        await state.UpsertVacationStateAsync(baseState with
        {
            Status = VacationStatuses.Pending, PayloadHash = hash, Attempts = attempts + 1, StartDate = mapping.StartDate, EndDate = mapping.EndDate,
        }, ct);

        var created = await api.RegisterAdjustmentAsync(new AdjustmentRegisterRequest
        {
            AdjustmentReasonId = reasonId,
            EmployeeExternalId = row.EmployeeExternalId,
            StartDate = mapping.StartDate,
            EndDate = mapping.EndDate,
            FullDay = true,
            Origem = VacationMapper.Origem,
            Observation = VacationMapper.Marker(row.Id),
            Status = mapping.Status,
        }, ct);
        ReferenceResolver.ThrowIfUnauthorized(created);

        if (created.Outcome == ApiOutcome.TransportError)
        {
            // Resultado desconhecido: continua "pending" e é reconciliado na próxima execução.
            context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Create, ItemStatuses.Failed, created.HttpStatus,
                $"resultado desconhecido, será reconciliado: {created.Message}"));
            return;
        }

        await RecordAsync(context, baseState, created, hash, attempts + 1, mapping, ItemActions.Create, itemKey, warnings, ct);
    }

    private async Task RecordAsync(
        SyncContext context, VacationState baseState, ApiResult<AdjustmentRecordDto> result, string hash, int attempts,
        VacationMapping mapping, string action, string itemKey, string? warnings, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            await state.UpsertVacationStateAsync(baseState with
            {
                RemoteAdjustmentId = result.Value!.Id,
                Status = VacationStatuses.Synced,
                PayloadHash = hash,
                Attempts = 0,
                LastError = null,
                StartDate = mapping.StartDate,
                EndDate = mapping.EndDate,
            }, ct);
            context.Add(new RunItem(EntityTypes.Vacation, itemKey, action,
                action == ItemActions.Create ? ItemStatuses.Created : ItemStatuses.Updated, result.HttpStatus, warnings));
            return;
        }

        var permanent = attempts >= Options.FeriasMaxTentativas;
        await state.UpsertVacationStateAsync(baseState with
        {
            Status = permanent ? VacationStatuses.FailedPermanent : VacationStatuses.Failed,
            PayloadHash = hash,
            Attempts = attempts,
            LastError = result.Message,
        }, ct);
        context.Add(new RunItem(EntityTypes.Vacation, itemKey, action, ItemStatuses.Failed, result.HttpStatus,
            permanent ? $"{result.Message} (desistindo após {attempts} tentativas)" : result.Message));
    }

    /// <summary>Lançamentos enviados cuja linha sumiu do feria2 (apagada no RHSenso) também são cancelados.</summary>
    private async Task CollectDeletedAsync(
        IReadOnlyList<VacationRow> rows, Dictionary<Guid, VacationState> vacations,
        List<(VacationState State, string Reason)> cancellations, CancellationToken ct)
    {
        var present = rows.Select(r => r.Id).ToHashSet();
        var candidates = vacations.Values
            .Where(v => v is { Status: VacationStatuses.Synced, RemoteAdjustmentId: not null } && !present.Contains(v.Feria2Id))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        // Fora da janela não é o mesmo que apagado: confere no banco.
        var stillExists = await source.ExistingVacationIdsAsync(candidates.Select(c => c.Feria2Id).ToList(), ct);
        cancellations.AddRange(candidates.Where(c => !stillExists.Contains(c.Feria2Id)).Select(c => (c, "apagada no RHSenso")));
    }

    private async Task CancelAsync(
        SyncContext context, long reasonId, Dictionary<string, EntityState> employees,
        List<(VacationState State, string Reason)> cancellations, CancellationToken ct)
    {
        if (cancellations.Count == 0)
        {
            return;
        }

        if (cancellations.Count > Options.MaxCancellationsPerRun)
        {
            context.Add(new RunItem(EntityTypes.Vacation, "-", ItemActions.Cancel, ItemStatuses.Failed,
                Message: $"max_cancellations_exceeded: {cancellations.Count} cancelamentos > Sync:MaxCancellationsPerRun ({Options.MaxCancellationsPerRun}). Nenhum foi enviado."));
            return;
        }

        foreach (var (vacation, reason) in cancellations)
        {
            var itemKey = $"{vacation.EmployeeExternalId}:{vacation.Feria2Id:D}";
            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Cancel, ItemStatuses.DryRun, Message: reason));
                continue;
            }

            if (!employees.TryGetValue(vacation.EmployeeExternalId, out var employee) || employee.RemoteId is null)
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Cancel, ItemStatuses.Failed, Message: "colaborador sem id no DP"));
                continue;
            }

            var remoteId = vacation.RemoteAdjustmentId!.Value;
            var cancel = new AdjustmentUpdateRequest
            {
                AdjustmentReasonId = reasonId,
                AdjustmenteReasonRecordId = remoteId,
                EmployeeId = employee.RemoteId.Value,
                StartDate = vacation.StartDate ?? 0,
                EndDate = vacation.EndDate ?? 0,
                FullDay = true,
                Origem = VacationMapper.Origem,
                Observation = VacationMapper.Marker(vacation.Feria2Id),
                Status = "REPROVADO",
                Excluded = true,
            };
            var result = await api.UpdateAdjustmentAsync(remoteId, cancel, ct);
            ReferenceResolver.ThrowIfUnauthorized(result);
            if (result.IsSuccess)
            {
                await state.UpsertVacationStateAsync(vacation with { Status = VacationStatuses.Cancelled, LastError = null }, ct);
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Cancel, ItemStatuses.Cancelled, result.HttpStatus, reason));
            }
            else
            {
                context.Add(new RunItem(EntityTypes.Vacation, itemKey, ItemActions.Cancel, ItemStatuses.Failed, result.HttpStatus, result.Message));
            }
        }
    }

    private static AdjustmentUpdateRequest UpdateRequest(
        Guid feria2Id, long remoteId, long employeeId, long reasonId, VacationMapping mapping, bool? excluded) => new()
    {
        AdjustmentReasonId = reasonId,
        AdjustmenteReasonRecordId = remoteId,
        EmployeeId = employeeId,
        StartDate = mapping.StartDate,
        EndDate = mapping.EndDate,
        FullDay = true,
        Origem = VacationMapper.Origem,
        Observation = VacationMapper.Marker(feria2Id),
        Status = mapping.Status,
        Excluded = excluded,
    };
}
