using System.Globalization;
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
/// <item>sem vínculo e CPF DESLIGADO no DP → pendência: criar de novo duplicaria a pessoa (o RH readmite no DP);</item>
/// <item>sem vínculo e CPF ausente no DP → cria, com o Código Externo = matrícula.</item>
/// </list>
/// Na simulação, cada pessoa também vira uma linha do relatório de vinculação (<see cref="VinculacaoReport"/>).
/// O Código Externo de quem já existe segue a regra do <see cref="CodigoExterno"/>. Escala e regra de ponto são
/// obrigatórias no DTO: na criação vão os padrões do config; na atualização reenviamos o que já está no DP.
/// Recusas do DP aos mesmos dados contam em solidesdp.colaborador_falha: no limite (Sync:ColaboradorMaxTentativas)
/// o colaborador vira pendência e só é reenviado quando os dados mudam ou uma vez por dia.
/// </summary>
public sealed class EmployeeStep(
    ISolidesDpClient api,
    ISourceReader source,
    IStateStore state,
    EmployeeMapper mapper,
    EpochDates dates,
    SyncOptionsAccessor syncOptions,
    ColaboradoresIgnorados ignorados,
    TimeProvider clock)
{
    /// <summary>Depois do limite de recusas, intervalo até a próxima tentativa com os mesmos dados.</summary>
    public static readonly TimeSpan IntervaloAposDesistir = TimeSpan.FromHours(24);

    private SyncOptions Options => syncOptions.Current;

    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(context.Cdempresa, EntityTypes.Employee, ct);
        var failures = await state.LoadEmployeeFailuresAsync(context.Cdempresa, ct);
        var ignoradosDaEmpresa = await ignorados.CarregarAsync(context.Cdempresa, Options, ct);
        foreach (var voltou in ignoradosDaEmpresa.Voltaram)
        {
            context.Add(new RunItem(EntityTypes.Employee, voltou.Rotulo, ItemActions.None, ItemStatuses.Warning,
                Message: FormattableString.Invariant(
                    $"ignorado_voltou: {voltou.Ignorado.NomeDp ?? "o cadastro"} (cadastro {voltou.Ignorado.CadastroDp} no DP) estava ignorado na conferência e apareceu ativo na folha; saiu da lista de ignorados e segue as regras da integração")));
        }

        ReportOutOfScope(context, plan, states);
        if (context.DryRun)
        {
            VinculacaoDoPlano(context, plan);
            if (context.CanQueryDp)
            {
                // Simulação com token: o relatório de vinculação mostra o cadastro atual do DP de todo mundo,
                // inclusive de quem já está vinculado, com o cargo atual.
                await EnsureIndexAsync(context, ct);
                await CarregarCargosDpAsync(context, ct);
            }
        }

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
                var motivo = string.Join("; ", mapping.Errors.Concat(mapping.Warnings));
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.None, ItemStatuses.Skipped, Message: motivo));
                Vinculacao(context, Linha(context, row, ResultadosVinculacao.Pendencia) with { Observacao = motivo });
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
                Vinculacao(context, Linha(context, row, ResultadosVinculacao.Pendencia) with { Observacao = string.Join("; ", blockers) });
                continue;
            }

            var warnings = mapping.Warnings.Count == 0 ? null : string.Join("; ", mapping.Warnings);
            candidates.Add(new Candidate(row, key, label, payload, HashOf(payload), warnings, current));
        }

        // Sem vínculo: antes de decidir criar, procura o CPF entre os ativos da conta no DP e, se não estiver
        // ativo, entre os desligados (criar de novo duplicaria a pessoa).
        if (candidates.Any(c => !IsLinked(c.Current)) && context.CanQueryDp)
        {
            var ativos = await EnsureIndexAsync(context, ct);
            if (candidates.Any(c => !IsLinked(c.Current) && ativos.ByCpf(c.Key).Count == 0))
            {
                await EnsureDesligadosAsync(context, ct);
            }
        }

        var decisions = new List<(Candidate Candidate, Decision Decision)>();
        foreach (var candidate in candidates)
        {
            var decision = Decide(context, candidate);
            if (decision.Blocked is { } blocked)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label, ItemActions.None, ItemStatuses.Blocked, Message: blocked));
                Vinculacao(context, Linha(context, candidate.Row, ResultadosVinculacao.Pendencia) with
                {
                    CadastroDp = decision.Remote?.Id,
                    NomeDp = decision.Remote?.Name?.Trim(),
                    CodigoExternoDp = decision.Remote?.ExternalId?.Trim(),
                    CargoDp = CargoDp(CargoAtual(context, decision.Remote?.JobRole)),
                    Observacao = blocked,
                });
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
                Vinculacao(context, LinhaDecidida(context, candidate, decision, ResultadosVinculacao.SemAlteracao));
                continue;
            }

            if (Aguardando(failures.GetValueOrDefault(candidate.Key), candidate.Hash) is { } aguardando)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label, ItemActions.None, ItemStatuses.Blocked, Message: aguardando));
                Vinculacao(context, LinhaDecidida(context, candidate, decision, ResultadosVinculacao.Pendencia) with { Observacao = aguardando });
                continue;
            }

            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label,
                    decision.Kind == DecisionKind.Create ? ItemActions.Create : ItemActions.Update, ItemStatuses.DryRun,
                    Message: Describe(context, candidate, decision)));
                Vinculacao(context, LinhaDecidida(context, candidate, decision, decision.Kind switch
                {
                    DecisionKind.Create => ResultadosVinculacao.Criar,
                    DecisionKind.Link => ResultadosVinculacao.Vincular,
                    _ => ResultadosVinculacao.Atualizar,
                }));
                continue;
            }

            await UpsertAsync(context, candidate, decision, failures, ct);
        }

        await SoNoDpAsync(context, plan, states, ignoradosDaEmpresa, ct);
    }

    /// <summary>
    /// Na simulação com token: quem está ATIVO no Sólides DP sem colaborador ativo correspondente no RHSenso (pelo CPF).
    /// A integração não desliga esse cadastro (só desliga quem ela vinculou); o relatório mostra o que o RHSenso diz
    /// da pessoa para o RH decidir. Com piloto preenchido não se aplica: fora do piloto todo mundo pareceria "só no DP".
    /// </summary>
    private async Task SoNoDpAsync(
        SyncContext context, EmployeePlan plan, Dictionary<string, EntityState> states, IgnoradosDaEmpresa ignoradosDaEmpresa, CancellationToken ct)
    {
        if (!context.DryRun || !context.CanQueryDp || Options.ExternalIdAllowList.Count > 0)
        {
            return;
        }

        var ativos = await EnsureIndexAsync(context, ct);
        var casos = await SoNoDpAnalise.AnalisarAsync(ativos.Todos, plan, states, Options, context.Cdempresa, context.Today, source, ct);
        var vinculos = states.Values
            .Where(s => s.RemoteId is not null)
            .GroupBy(s => s.RemoteId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var quantosIgnorados = 0;
        foreach (var caso in casos)
        {
            // Ignorado na conferência (cadastro só do Sólides DP, por decisão do RH): fica fora do relatório.
            if (ignoradosDaEmpresa.Contem(caso.Dp.Id, caso.Cpf))
            {
                quantosIgnorados++;
                continue;
            }

            vinculos.TryGetValue(caso.Dp.Id, out var vinculo);
            context.Vinculacao.Add(new VinculacaoLinha
            {
                Rotulo = caso.Saida?.Label ?? string.Empty,
                Nome = caso.Saida is null ? null : plan.NomesPorRotulo.GetValueOrDefault(caso.Saida.Label),
                Cpf = caso.Cpf,
                Resultado = ResultadosVinculacao.SoNoDp,
                CadastroDp = caso.Dp.Id,
                NomeDp = caso.Dp.Name?.Trim(),
                CodigoExternoDp = caso.Dp.ExternalId?.Trim(),
                CargoDp = CargoDp(CargoAtual(context, caso.Dp.JobRole)),
                Observacao = Join(caso.Motivo, SoNoDpVinculo(vinculo)),
            });
        }

        var total = casos.Count - quantosIgnorados;
        var ignoradosTexto = quantosIgnorados == 0
            ? string.Empty
            : FormattableString.Invariant($" Outros {quantosIgnorados} estão ignorados na conferência (só no Sólides DP por decisão do RH).");
        if (total > 0)
        {
            context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.None, ItemStatuses.Warning,
                Message: FormattableString.Invariant(
                    $"so_no_dp: {total} cadastro(s) ativo(s) no Sólides DP sem colaborador ativo no RHSenso; a integração não mexe neles. Ver \"Só no Sólides DP\" no relatório de vinculação.") + ignoradosTexto));
        }
    }

    /// <summary>Carrega uma vez os cargos da conta no DP (só na simulação). Sem eles, o relatório sai sem o cargo atual.</summary>
    private async Task CarregarCargosDpAsync(SyncContext context, CancellationToken ct)
    {
        if (context.DpCargos.Count > 0)
        {
            return;
        }

        var cargos = await api.FindAllJobRolesAsync(ct);
        ReferenceResolver.ThrowIfUnauthorized(cargos);
        if (!cargos.IsSuccess || cargos.Value is null)
        {
            context.Add(new RunItem(EntityTypes.JobRole, "-", ItemActions.None, ItemStatuses.Warning,
                Message: $"Não foi possível listar os cargos do Sólides DP ({cargos.Outcome} {cargos.Message}); o relatório de vinculação sai sem o cargo atual."));
            return;
        }

        foreach (var cargo in cargos.Value.Where(c => c.Id > 0))
        {
            context.DpCargos[cargo.Id] = cargo;
        }
    }

    /// <summary>
    /// Cargo atual de um cadastro do DP. A lista de colaboradores pode trazer o cargo só com o id: completa com o
    /// código e a descrição da lista de cargos da conta (carregada na simulação).
    /// </summary>
    private static JobRoleDto? CargoAtual(SyncContext context, JobRoleDto? cargo) =>
        cargo is { Id: > 0 } && context.DpCargos.TryGetValue(cargo.Id, out var completo) ? completo : cargo;

    /// <summary>Quem a integração já vinculou e agora aparece só no DP: CPF trocado no RHSenso ou readmitido no DP.</summary>
    private static string? SoNoDpVinculo(EntityState? vinculo) => vinculo switch
    {
        null => null,
        { Status: EntityStatuses.Dismissed } => "a integração já desligou este cadastro, mas ele está ativo no Sólides DP (readmitido lá?)",
        { } outro => $"vinculado pela integração à matrícula {outro.Matricula ?? "?"}: conferir se o CPF foi alterado no RHSenso",
    };

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

    /// <summary>Carrega uma vez por execução os colaboradores desligados da conta no DP (só quando alguém seria criado).</summary>
    public async Task<DpDesligadosIndex> EnsureDesligadosAsync(SyncContext context, CancellationToken ct)
    {
        if (context.DpDesligados is { } loaded)
        {
            return loaded;
        }

        var ativos = await EnsureIndexAsync(context, ct);
        var all = await api.FindAllEmployeesIncludingFiredAsync(ct);
        ReferenceResolver.ThrowIfUnauthorized(all);
        if (!all.IsSuccess || all.Value is null)
        {
            throw new SyncAbortedException(
                $"Falha ao listar os colaboradores desligados do Sólides DP (necessário para não recriar quem já existe lá): {all.Outcome} {all.Message}");
        }

        context.DpDesligados = DpDesligadosIndex.From(all.Value, ativos);
        return context.DpDesligados;
    }

    private static bool IsLinked(EntityState? current) =>
        current is { RemoteId: not null } && current.Status != EntityStatuses.Dismissed;

    private Decision Decide(SyncContext context, Candidate candidate)
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

        // Não está ativo, mas existe DESLIGADO na conta (ex-funcionário readmitido no RHSenso, ou desligado à mão no DP):
        // criar outro cadastro duplicaria a pessoa. O RH readmite no DP e a próxima execução vincula pelo CPF.
        if (context.DpDesligados?.ByCpf(candidate.Key) is [var desligado, ..])
        {
            return new Decision(DecisionKind.Create, null, desligado, Desligado(desligado));
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

    /// <summary>
    /// Mensagem da pendência se o DP já recusou estes mesmos dados o número máximo de vezes e ainda não passou
    /// <see cref="IntervaloAposDesistir"/> desde a última recusa; null = pode enviar.
    /// </summary>
    private string? Aguardando(EmployeeFailure? failure, string hash)
    {
        if (failure is null || failure.PayloadHash != hash || failure.Attempts < Options.ColaboradorMaxTentativas)
        {
            return null;
        }

        var proxima = failure.LastFailure + IntervaloAposDesistir;
        return clock.GetUtcNow() >= proxima ? null : Recusado(failure.Attempts, failure.FirstFailure, failure.LastError, proxima);
    }

    private string Recusado(int tentativas, DateTimeOffset desde, string? erro, DateTimeOffset proxima)
    {
        var vezes = tentativas.ToString(CultureInfo.InvariantCulture);
        return $"recusado_pelo_dp: o Sólides DP recusou {vezes} vezes seguidas os mesmos dados (desde {Local(desde)}): {erro}. " +
               $"Corrija o cadastro no RHSenso (o envio é refeito na próxima execução) ou no Sólides DP; senão a integração tenta de novo em {Local(proxima)}";
    }

    private string Local(DateTimeOffset quando) =>
        TimeZoneInfo.ConvertTime(quando, dates.Zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Conta a recusa do DP (só <see cref="ApiOutcome.Rejected"/>; queda e timeout não contam) e devolve o item da execução.</summary>
    private async Task<RunItem> RegistrarRecusaAsync(
        SyncContext context, Candidate candidate, Dictionary<string, EmployeeFailure> failures, string action, int? httpStatus, string? message, CancellationToken ct)
    {
        var agora = clock.GetUtcNow();
        var anterior = failures.GetValueOrDefault(candidate.Key);
        var mesmosDados = anterior is not null && anterior.PayloadHash == candidate.Hash;
        var failure = new EmployeeFailure
        {
            ExternalId = candidate.Key,
            PayloadHash = candidate.Hash,
            Attempts = mesmosDados ? anterior!.Attempts + 1 : 1,
            LastError = message,
            FirstFailure = mesmosDados ? anterior!.FirstFailure : agora,
            LastFailure = agora,
        };
        await state.UpsertEmployeeFailureAsync(context.Cdempresa, failure, ct);
        failures[candidate.Key] = failure;

        // No limite vira pendência: reenviar igual a cada execução não resolve, e a falha repetida não avisa de novo.
        return failure.Attempts >= Options.ColaboradorMaxTentativas
            ? new RunItem(EntityTypes.Employee, candidate.Label, action, ItemStatuses.Blocked, httpStatus,
                Recusado(failure.Attempts, failure.FirstFailure, message, agora + IntervaloAposDesistir))
            : new RunItem(EntityTypes.Employee, candidate.Label, action, ItemStatuses.Failed, httpStatus, message);
    }

    private async Task UpsertAsync(
        SyncContext context, Candidate candidate, Decision decision, Dictionary<string, EmployeeFailure> failures, CancellationToken ct)
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
                await EnsureDesligadosAsync(context, ct);
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
            // Quem já existe no DP mantém o início da vigência de lá (cadastro manual com data própria).
            EffectiveDate = remote?.EffectiveDate ?? payload.EffectiveDate,
            JobRoleStartDate = JobRoleStartDate(context, payload, remote),
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
            context.Add(result.Outcome == ApiOutcome.Rejected
                ? await RegistrarRecusaAsync(context, candidate, failures, action, result.HttpStatus, message, ct)
                : new RunItem(EntityTypes.Employee, label, action, ItemStatuses.Failed, result.HttpStatus, message));
            return;
        }

        if (failures.Remove(key))
        {
            await state.ClearEmployeeFailureAsync(context.Cdempresa, key, ct);
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

    /// <summary>
    /// Início no cargo: na criação, o início da vigência; para quem já existe, hoje, e só quando o cargo do DP é
    /// outro (sem essa data o DP ignora a troca de cargo). Cargo igual: não envia, para não criar histórico à toa.
    /// </summary>
    private long? JobRoleStartDate(SyncContext context, EmployeeRequest payload, EmployeeDto? remote)
    {
        if (remote is null)
        {
            return payload.EffectiveDate;
        }

        var cargoNoDp = remote.JobRole?.ExternalId?.Trim();
        return string.Equals(cargoNoDp, payload.JobRoleExternalId, StringComparison.Ordinal)
            ? null
            : Math.Max(dates.StartOfDay(context.Today), remote.EffectiveDate ?? long.MinValue);
    }

    /// <summary>Escala/regra enviadas: padrões na criação; para quem já existe, a escala atual do DP e as datas já usadas.</summary>
    private ScheduleAssignment ScheduleFor(SyncContext context, EmployeeRow row, EmployeeDto? remote, EntityState? current)
    {
        var effective = remote?.EffectiveDate ?? dates.StartOfDay(EmployeeMapper.EffectiveDate(row, Options.GoLiveDate));
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

    private string Desligado(EmployeeDto desligado)
    {
        var quando = desligado.ResignationDate is { } ms
            ? ", desligado em " + dates.ToDate(ms).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : string.Empty;
        return FormattableString.Invariant(
            $"cpf_desligado_no_dp: este CPF está DESLIGADO no Sólides DP (cadastro {desligado.Id}{quando}); a integração não cria outro cadastro para não duplicar a pessoa. ") +
            "Readmita no Sólides DP (a próxima execução vincula pelo CPF) ou fale com a RHSenso";
    }

    /// <summary>Na simulação, quem ficou fora do envio já na separação do RHSenso (CPF inválido, duplicado, fora do escopo).</summary>
    private static void VinculacaoDoPlano(SyncContext context, EmployeePlan plan)
    {
        foreach (var item in plan.Skipped.Where(i => i.EntityType == EntityTypes.Employee
                                                     && i.Message?.StartsWith("superseded", StringComparison.Ordinal) != true))
        {
            context.Vinculacao.Add(new VinculacaoLinha
            {
                Rotulo = item.ExternalId,
                Nome = plan.NomesPorRotulo.GetValueOrDefault(item.ExternalId),
                Resultado = ResultadosVinculacao.Pendencia,
                Observacao = item.Message,
            });
        }

        foreach (var row in plan.OutOfScope.Values)
        {
            context.Vinculacao.Add(Linha(context, row, ResultadosVinculacao.ForaDoEscopo) with
            {
                Observacao = FormattableString.Invariant(
                    $"ativo na filial {row.Cdfilial}, que não está marcada (ou está inativa): nada é enviado nem desligado"),
            });
        }
    }

    private static void Vinculacao(SyncContext context, VinculacaoLinha linha)
    {
        if (context.DryRun)
        {
            context.Vinculacao.Add(linha);
        }
    }

    private static VinculacaoLinha Linha(SyncContext context, EmployeeRow row, string resultado) => new()
    {
        Rotulo = row.Rotulo,
        Nome = row.Nome?.Trim(),
        Cpf = row.Chave,
        Filial = row.Cdfilial,
        Resultado = resultado,
        CargoRhSenso = CargoRhSenso(context, EmployeeMapper.Clean(row.Cargo)),
    };

    /// <summary>Linha de quem passou por todas as checagens: o que o envio faria e com qual cadastro do DP.</summary>
    private static VinculacaoLinha LinhaDecidida(SyncContext context, Candidate candidate, Decision decision, string resultado)
    {
        var linha = Linha(context, candidate.Row, resultado) with
        {
            CargoRhSenso = CargoRhSenso(context, candidate.Payload.JobRoleExternalId),
            Observacao = candidate.Warnings,
        };
        var matricula = candidate.Row.Nomatric.Trim();

        switch (decision.Kind)
        {
            case DecisionKind.Link when decision.Remote is { } remote:
                return ComCadastroDp(context, linha, candidate, remote, semAlteracao: false);
            case DecisionKind.Create:
                return linha with
                {
                    CodigoExternoFica = matricula,
                    Observacao = context.CanQueryDp
                        ? candidate.Warnings
                        : Join("sem token: o Sólides DP não foi consultado; pode virar vínculo pelo CPF", candidate.Warnings),
                };
            default:
                // Já vinculado: o cadastro atual do DP quando a simulação consultou o DP; senão, o guardado no vínculo.
                if (candidate.Current?.RemoteId is { } id && context.DpEmployees?.ById(id) is { } atual)
                {
                    return ComCadastroDp(context, linha, candidate, atual, semAlteracao: resultado == ResultadosVinculacao.SemAlteracao);
                }

                return linha with
                {
                    CadastroDp = candidate.Current?.RemoteId,
                    CodigoExternoDp = candidate.Current?.CodigoExterno,
                    CodigoExternoFica = candidate.Current?.CodigoExterno,
                };
        }
    }

    /// <summary>Linha com o cadastro do DP ao lado: nome, Código Externo e cargo atuais e o que fica depois do envio.</summary>
    private static VinculacaoLinha ComCadastroDp(SyncContext context, VinculacaoLinha linha, Candidate candidate, EmployeeDto remote, bool semAlteracao)
    {
        var cargo = CargoAtual(context, remote.JobRole);
        return linha with
        {
            CadastroDp = remote.Id,
            NomeDp = remote.Name?.Trim(),
            CodigoExternoDp = remote.ExternalId?.Trim(),
            // Sem alteração nada é enviado: fica o que já está no DP.
            CodigoExternoFica = semAlteracao ? remote.ExternalId?.Trim() : CodigoExterno.ParaEnviar(remote.ExternalId, candidate.Row.Nomatric),
            CargoDp = CargoDp(cargo),
            CargoMuda = !semAlteracao && !string.Equals(cargo?.ExternalId?.Trim(), candidate.Payload.JobRoleExternalId, StringComparison.Ordinal),
        };
    }

    private static string? CargoRhSenso(SyncContext context, string? codigo) =>
        codigo is null ? null
        : context.JobRoleDescriptions.TryGetValue(codigo, out var descricao) ? $"{codigo} {descricao}"
        : codigo;

    private static string? CargoDp(JobRoleDto? cargo)
    {
        if (cargo is null)
        {
            return null;
        }

        var texto = string.Join(' ', new[] { cargo.ExternalId?.Trim(), cargo.Description?.Trim() }.Where(t => !string.IsNullOrEmpty(t)));
        return texto.Length == 0 ? null : texto;
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

    /// <summary>
    /// Versão da regra de envio do colaborador. Incrementar quando o que vai para o DP mudar sem o RHSenso mudar:
    /// todos os colaboradores são reenviados uma vez. 2 = vigência do DP preservada e início no cargo.
    /// </summary>
    public const int RegraEnvio = 2;

    /// <summary>O hash ignora o Código Externo (decidido pela regra a cada envio) e o id do DP.</summary>
    private static string HashOf(EmployeeRequest payload) =>
        PayloadHasher.Hash(new { RegraEnvio, Payload = payload with { ExternalId = null, TangerinoId = null } });

    private static string Describe(SyncContext context, Candidate candidate, Decision decision)
    {
        var payload = candidate.Payload;
        var what = decision.Kind switch
        {
            DecisionKind.Link => $"vincular pelo CPF ao cadastro {decision.RemoteId} do DP" + LinkChange(context, candidate, decision),
            DecisionKind.Create when context.CanQueryDp => "criar (CPF não está ativo no DP)",
            DecisionKind.Create => "criar (sem token: o DP não foi consultado; pode virar vínculo pelo CPF)",
            _ => "atualizar",
        };
        return Join($"{what} | {payload.Name} | cargo {payload.JobRoleExternalId} | local {payload.WorkplaceExternalId}", candidate.Warnings)!;
    }

    private static string LinkChange(SyncContext context, Candidate candidate, Decision decision)
    {
        var atual = decision.Remote?.ExternalId;
        var changes = new List<string>();
        if (CodigoExterno.DescreverMudanca(atual, CodigoExterno.ParaEnviar(atual, candidate.Row.Nomatric)) is { } codigo)
        {
            changes.Add(codigo);
        }

        if (CargoAtual(context, decision.Remote?.JobRole) is { } cargo
            && !string.Equals(cargo.ExternalId?.Trim(), candidate.Payload.JobRoleExternalId, StringComparison.Ordinal))
        {
            changes.Add($"cargo '{cargo.Description}' → {candidate.Payload.JobRoleExternalId} a partir de hoje");
        }

        return changes.Count == 0 ? string.Empty : $" ({string.Join("; ", changes)})";
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
    private readonly Dictionary<long, EmployeeDto> _byId = new();
    private readonly List<EmployeeDto> _todos = [];

    public int Count { get; private set; }

    /// <summary>Todos os ativos da conta, na ordem em que o DP devolveu.</summary>
    public IReadOnlyList<EmployeeDto> Todos => _todos;

    /// <summary>O cadastro (id do DP) está entre os ativos.</summary>
    public bool Contains(long id) => _byId.ContainsKey(id);

    /// <summary>O cadastro ativo com este id do DP, ou null.</summary>
    public EmployeeDto? ById(long id) => _byId.GetValueOrDefault(id);

    public static DpEmployeeIndex From(IEnumerable<EmployeeDto> employees)
    {
        var index = new DpEmployeeIndex();
        foreach (var employee in employees.Where(e => e.Fired != true))
        {
            index.Count++;
            index._byId[employee.Id] = employee;
            index._todos.Add(employee);
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

/// <summary>
/// Colaboradores DESLIGADOS de uma conta do Sólides DP, por CPF: quem não está ativo mas já existe lá não é criado de novo.
/// Desligado = está na lista com os demitidos (showFired=1) e não está na de ativos; não depende do campo "fired" vir
/// preenchido na resposta.
/// </summary>
public sealed class DpDesligadosIndex
{
    private readonly Dictionary<string, List<EmployeeDto>> _byCpf = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    /// <param name="todos">Colaboradores da conta, inclusive os desligados (find-all com showFired=1).</param>
    /// <param name="ativos">Os ativos da mesma conta (find-all com showFired=0).</param>
    public static DpDesligadosIndex From(IEnumerable<EmployeeDto> todos, DpEmployeeIndex ativos)
    {
        var index = new DpDesligadosIndex();
        foreach (var employee in todos.Where(e => !ativos.Contains(e.Id) && e.Fired != false))
        {
            if (Documents.NormalizeCpf(employee.Cpf).Value is not { } cpf)
            {
                continue;
            }

            index.Count++;
            if (!index._byCpf.TryGetValue(cpf, out var list))
            {
                index._byCpf[cpf] = list = [];
            }

            list.Add(employee);
        }

        // O desligamento mais recente primeiro (é o que aparece na mensagem).
        foreach (var list in index._byCpf.Values)
        {
            list.Sort((a, b) => Nullable.Compare(b.ResignationDate, a.ResignationDate));
        }

        return index;
    }

    public IReadOnlyList<EmployeeDto> ByCpf(string cpf) => _byCpf.TryGetValue(cpf, out var list) ? list : [];
}
