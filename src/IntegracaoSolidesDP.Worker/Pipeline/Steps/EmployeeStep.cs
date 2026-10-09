using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Colaboradores ativos de uma empresa. A identidade no Sólides DP é o CPF, dentro da conta da empresa:
/// <list type="bullet">
/// <item>já vinculado (solidesdp.colaborador_vinculo) → atualiza pelo id do DP (tangerinoId);</item>
/// <item>sem vínculo, mas o CPF já está ativo no DP (cadastro manual do RH) → vincula e atualiza;</item>
/// <item>sem vínculo e CPF ausente no DP → cria, com o Código Externo = matrícula.</item>
/// </list>
/// O Código Externo de quem já existe segue a regra do <see cref="CodigoExterno"/>. Escala e regra de ponto são
/// obrigatórias no DTO: na criação vão os padrões do config; na atualização reenviamos o que já está no DP.
/// </summary>
public sealed class EmployeeStep(
    ISolidesDpClient api,
    IStateStore state,
    EmployeeMapper mapper,
    EpochDates dates,
    SyncOptionsAccessor syncOptions)
{
    private SyncOptions Options => syncOptions.Current;

    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(context.Cdempresa, EntityTypes.Employee, ct);
        ReportOutOfScope(context, plan, states);

        var candidates = new List<Candidate>();
        foreach (var row in plan.Active.OrderBy(r => r.Nomatric, StringComparer.Ordinal))
        {
            var key = row.Chave!;
            var label = row.Rotulo;
            states.TryGetValue(key, out var current);

            long? companyId = null;
            if (Options.CompanyMode == CompanyMode.ResolveByCnpj && !context.DryRun)
            {
                var cnpj = EmployeeMapper.Digits(row.Cnpj);
                if (cnpj is null || !context.CompanyIdsByCnpj.TryGetValue(cnpj, out var id))
                {
                    context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.None, ItemStatuses.Blocked,
                        Message: $"company_not_found: CNPJ '{cnpj ?? "vazio"}' da filial {row.Cdempresa}-{row.Cdfilial}"));
                    continue;
                }

                companyId = id;
            }

            var mapping = mapper.Map(row, Options.GoLiveDate, companyId);
            if (!mapping.IsValid)
            {
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.None, ItemStatuses.Skipped,
                    Message: string.Join("; ", mapping.Errors.Concat(mapping.Warnings))));
                continue;
            }

            var payload = mapping.Payload!;
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
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.None, ItemStatuses.Blocked, Message: string.Join("; ", blockers)));
                continue;
            }

            var warnings = mapping.Warnings.Count == 0 ? null : string.Join("; ", mapping.Warnings);
            candidates.Add(new Candidate(row, key, label, payload, HashOf(payload), warnings, current));
        }

        // Sem vínculo: antes de decidir criar, procura o CPF entre os ativos da conta no DP.
        if (candidates.Any(c => !IsLinked(c.Current)) && context.CanQueryDp)
        {
            await EnsureIndexAsync(context, ct);
        }

        var decisions = new List<(Candidate Candidate, Decision Decision)>();
        foreach (var candidate in candidates)
        {
            var decision = Decide(context, candidate);
            if (decision.Blocked is { } blocked)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label, ItemActions.None, ItemStatuses.Blocked, Message: blocked));
                continue;
            }

            decisions.Add((candidate, decision));
        }

        var creates = decisions.Count(d => d.Decision.Kind == DecisionKind.Create);
        if (!context.DryRun && creates > Options.MaxCreatesPerRun)
        {
            context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.Create, ItemStatuses.Failed,
                Message: $"max_creates_exceeded: {creates} colaboradores novos > Sync:MaxCreatesPerRun ({Options.MaxCreatesPerRun}). Nada foi enviado."));
            return;
        }

        foreach (var (candidate, decision) in decisions)
        {
            var unchanged = decision.Kind == DecisionKind.Update
                            && candidate.Current is { Status: EntityStatuses.Synced }
                            && candidate.Current.PayloadHash == candidate.Hash;
            if (unchanged)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label, ItemActions.None, ItemStatuses.Unchanged));
                continue;
            }

            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label,
                    decision.Kind == DecisionKind.Create ? ItemActions.Create : ItemActions.Update, ItemStatuses.DryRun,
                    Message: Describe(candidate, decision, context.CanQueryDp)));
                continue;
            }

            await UpsertAsync(context, candidate, decision, ct);
        }
    }

    /// <summary>Carrega uma vez por execução os colaboradores ativos da conta no DP.</summary>
    public async Task<DpEmployeeIndex> EnsureIndexAsync(SyncContext context, CancellationToken ct)
    {
        if (context.DpEmployees is { } loaded)
        {
            return loaded;
        }

        var all = await api.FindAllEmployeesAsync(ct);
        ReferenceResolver.ThrowIfUnauthorized(all);
        if (!all.IsSuccess || all.Value is null)
        {
            throw new SyncAbortedException(
                $"Falha ao listar os colaboradores do Sólides DP (necessário para vincular pelo CPF): {all.Outcome} {all.Message}");
        }

        context.DpEmployees = DpEmployeeIndex.From(all.Value);
        return context.DpEmployees;
    }

    private static bool IsLinked(EntityState? current) =>
        current is { RemoteId: not null } && current.Status != EntityStatuses.Dismissed;

    private static Decision Decide(SyncContext context, Candidate candidate)
    {
        if (IsLinked(candidate.Current))
        {
            return new Decision(DecisionKind.Update, candidate.Current!.RemoteId, null);
        }

        if (context.DpEmployees is not { } index)
        {
            // Dry-run sem token: não dá para saber se o CPF já está no DP.
            return new Decision(DecisionKind.Create, null, null);
        }

        var sameCpf = index.ByCpf(candidate.Key);
        if (sameCpf.Count > 1)
        {
            return new Decision(DecisionKind.Link, null, null,
                $"cpf_duplicado_no_dp: {sameCpf.Count} cadastros ativos com este CPF no Sólides DP (ids {string.Join(", ", sameCpf.Select(e => e.Id))}); corrigir no DP");
        }

        if (sameCpf.Count == 1)
        {
            return new Decision(DecisionKind.Link, sameCpf[0].Id, sameCpf[0]);
        }

        // O register com allowUpdate procura pelo externalId: se a matrícula já for o Código Externo de OUTRO
        // colaborador ativo, a criação sobrescreveria aquele cadastro.
        var matricula = candidate.Row.Nomatric.Trim();
        var sameCode = index.ByCodigoExterno(matricula);
        if (sameCode.Count > 0)
        {
            return new Decision(DecisionKind.Create, null, null,
                $"codigo_externo_em_uso: o Código Externo {matricula} já é de outro colaborador ativo no Sólides DP " +
                $"(id {string.Join(", ", sameCode.Select(e => e.Id))}, com outro CPF); conferir no DP antes de criar");
        }

        return new Decision(DecisionKind.Create, null, null);
    }

    private async Task UpsertAsync(SyncContext context, Candidate candidate, Decision decision, CancellationToken ct)
    {
        var (row, key, label, payload, hash, warnings, current) = candidate;
        var kind = decision.Kind;
        var remote = decision.Remote;

        if (decision.RemoteId is { } remoteId)
        {
            // Lido de novo pelo id: o Código Externo, a escala e a situação atuais vêm do próprio cadastro.
            var found = await api.FindEmployeeByIdAsync(remoteId, ct);
            ReferenceResolver.ThrowIfUnauthorized(found);
            if (found.IsSuccess)
            {
                remote = found.Value;
            }
            else if (found.Outcome == ApiOutcome.NotFound && kind == DecisionKind.Update)
            {
                // O cadastro vinculado sumiu do DP (apagado lá): procura o CPF de novo; sem ele, cria.
                await EnsureIndexAsync(context, ct);
                var again = Decide(context, candidate with { Current = null });
                if (again.Blocked is { } blocked)
                {
                    context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.None, ItemStatuses.Blocked,
                        Message: $"o cadastro vinculado (id {remoteId}) não existe mais no DP; {blocked}"));
                    return;
                }

                kind = again.Kind;
                remote = again.Remote;
                current = null;
            }
            else
            {
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Update, ItemStatuses.Failed, found.HttpStatus, found.Message));
                return;
            }
        }

        var schedule = ScheduleFor(context, row, remote, current);
        var codigoExterno = remote is null ? row.Nomatric.Trim() : CodigoExterno.ParaEnviar(remote.ExternalId, row.Nomatric);
        var request = payload with
        {
            TangerinoId = remote?.Id,
            ExternalId = codigoExterno,
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
            context.Add(new RunItem(EntityTypes.Employee, label, action, ItemStatuses.Failed, result.HttpStatus, message));
            return;
        }

        var linked = kind == DecisionKind.Link && remote is not null;
        var origem = linked
            ? OrigensVinculo.VinculadoCpf
            : current?.Origem ?? OrigensVinculo.Criado;

        await state.UpsertEntityStateAsync(context.Cdempresa, new EntityState
        {
            EntityType = EntityTypes.Employee,
            ExternalId = key,
            RemoteId = result.Value is { Id: > 0 } v ? v.Id : remote?.Id,
            PayloadHash = hash,
            Status = EntityStatuses.Synced,
            ExtraJson = JsonSerializer.Serialize(schedule, SolidesDpJson.Options),
            CodigoExterno = codigoExterno,
            Matricula = row.Nomatric.Trim(),
            Cdfilial = row.Cdfilial,
            Origem = origem,
        }, ct);

        var change = remote is null ? null : CodigoExterno.DescreverMudanca(remote.ExternalId, codigoExterno);
        var (status, note) = (remote, linked) switch
        {
            (null, _) => (ItemStatuses.Created, (string?)null),
            (_, true) => (ItemStatuses.Adopted, $"vinculado pelo CPF ao cadastro {remote.Id} do DP"),
            _ => (ItemStatuses.Updated, null),
        };
        context.Add(new RunItem(EntityTypes.Employee, label, action, status, result.HttpStatus, Join(note, change, warnings)));
    }

    /// <summary>Escala/regra enviadas: padrões na criação; para quem já existe, a escala atual do DP e as datas já usadas.</summary>
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

    /// <summary>Ativo numa filial fora do escopo e já vinculado: avisa que nada é enviado nem desligado.</summary>
    private static void ReportOutOfScope(SyncContext context, EmployeePlan plan, Dictionary<string, EntityState> states)
    {
        foreach (var (key, row) in plan.OutOfScope.OrderBy(o => o.Value.Nomatric, StringComparer.Ordinal))
        {
            if (IsLinked(states.GetValueOrDefault(key)))
            {
                context.Add(new RunItem(EntityTypes.Employee, row.Rotulo, ItemActions.None, ItemStatuses.Skipped,
                    Message: FormattableString.Invariant(
                        $"fora_do_escopo: ativo na filial {row.Cdfilial}, que não está marcada (ou está inativa); nada é enviado nem desligado")));
            }
        }
    }

    /// <summary>O hash ignora o Código Externo (decidido pela regra a cada envio) e o id do DP.</summary>
    private static string HashOf(EmployeeRequest payload) => PayloadHasher.Hash(payload with { ExternalId = null, TangerinoId = null });

    private static string Describe(Candidate candidate, Decision decision, bool consultedDp)
    {
        var payload = candidate.Payload;
        var what = decision.Kind switch
        {
            DecisionKind.Link => $"vincular pelo CPF ao cadastro {decision.RemoteId} do DP" + LinkChange(candidate, decision),
            DecisionKind.Create when consultedDp => "criar (CPF não está ativo no DP)",
            DecisionKind.Create => "criar (sem token: o DP não foi consultado; pode virar vínculo pelo CPF)",
            _ => "atualizar",
        };
        return Join($"{what} | {payload.Name} | cargo {payload.JobRoleExternalId} | local {payload.WorkplaceExternalId}", candidate.Warnings)!;
    }

    private static string LinkChange(Candidate candidate, Decision decision)
    {
        var atual = decision.Remote?.ExternalId;
        var change = CodigoExterno.DescreverMudanca(atual, CodigoExterno.ParaEnviar(atual, candidate.Row.Nomatric));
        return change is null ? string.Empty : $" ({change})";
    }

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(" | ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : text;
    }

    private sealed record Candidate(
        EmployeeRow Row, string Key, string Label, EmployeeRequest Payload, string Hash, string? Warnings, EntityState? Current);

    private enum DecisionKind
    {
        Create,
        Link,
        Update,
    }

    private sealed record Decision(DecisionKind Kind, long? RemoteId, EmployeeDto? Remote, string? Blocked = null);
}

/// <summary>Escala e regra de ponto enviadas ao DP (guardadas no estado para as atualizações).</summary>
public sealed record ScheduleAssignment(long? WorkScheduleId, long WorkScheduleDate, string? PunchRuleExternalId, long PunchRuleDate);

/// <summary>Colaboradores ativos de uma conta do Sólides DP, por CPF e por Código Externo.</summary>
public sealed class DpEmployeeIndex
{
    private readonly Dictionary<string, List<EmployeeDto>> _byCpf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EmployeeDto>> _byCodigo = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public static DpEmployeeIndex From(IEnumerable<EmployeeDto> employees)
    {
        var index = new DpEmployeeIndex();
        foreach (var employee in employees.Where(e => e.Fired != true))
        {
            index.Count++;
            if (Documents.NormalizeCpf(employee.Cpf).Value is { } cpf)
            {
                Add(index._byCpf, cpf, employee);
            }

            if (EmployeeMapper.Clean(employee.ExternalId) is { } codigo)
            {
                Add(index._byCodigo, codigo, employee);
            }
        }

        return index;
    }

    public IReadOnlyList<EmployeeDto> ByCpf(string cpf) => _byCpf.TryGetValue(cpf, out var list) ? list : [];

    public IReadOnlyList<EmployeeDto> ByCodigoExterno(string codigo) => _byCodigo.TryGetValue(codigo, out var list) ? list : [];

    private static void Add(Dictionary<string, List<EmployeeDto>> map, string key, EmployeeDto employee)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(employee);
    }
}
