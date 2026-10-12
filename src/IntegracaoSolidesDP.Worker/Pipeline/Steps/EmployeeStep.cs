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
        if (context.DryRun)
        {
            VinculacaoDoPlano(context, plan);
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
                    CargoDp = CargoDp(decision.Remote?.JobRole),
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

            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, candidate.Label,
                    decision.Kind == DecisionKind.Create ? ItemActions.Create : ItemActions.Update, ItemStatuses.DryRun,
                    Message: Describe(candidate, decision, context.CanQueryDp)));
                Vinculacao(context, LinhaDecidida(context, candidate, decision, decision.Kind switch
                {
                    DecisionKind.Create => ResultadosVinculacao.Criar,
                    DecisionKind.Link => ResultadosVinculacao.Vincular,
                    _ => ResultadosVinculacao.Atualizar,
                }));
                continue;
            }

            await UpsertAsync(context, candidate, decision, ct);
        }

        await SoNoDpAsync(context, plan, states, ct);
    }

    /// <summary>
    /// Na simulação com token: quem está ATIVO no Sólides DP sem colaborador ativo correspondente no RHSenso (pelo CPF).
    /// A integração não desliga esse cadastro (só desliga quem ela vinculou); o relatório mostra o que o RHSenso diz
    /// da pessoa para o RH decidir. Com piloto preenchido não se aplica: fora do piloto todo mundo pareceria "só no DP".
    /// </summary>
    private async Task SoNoDpAsync(SyncContext context, EmployeePlan plan, Dictionary<string, EntityState> states, CancellationToken ct)
    {
        if (!context.DryRun || !context.CanQueryDp || Options.ExternalIdAllowList.Count > 0)
        {
            return;
        }

        var ativos = await EnsureIndexAsync(context, ct);
        var saidas = plan.Departures.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var vinculos = states.Values
            .Where(s => s.RemoteId is not null)
            .GroupBy(s => s.RemoteId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var total = 0;
        foreach (var dp in ativos.Todos)
        {
            var cpf = Documents.NormalizeCpf(dp.Cpf).Value;
            if (cpf is not null && plan.CpfsAtivosNoRhSenso.Contains(cpf))
            {
                continue;
            }

            Departure? saida = null;
            if (cpf is not null && saidas.TryGetValue(cpf, out var encontrada))
            {
                // Já vinculado: o DismissalStep desliga (linha "Desligar"); não é decisão do RH.
                if (IsLinked(states.GetValueOrDefault(cpf)))
                {
                    continue;
                }

                saida = encontrada;
            }

            vinculos.TryGetValue(dp.Id, out var vinculo);
            context.Vinculacao.Add(new VinculacaoLinha
            {
                Rotulo = saida?.Label ?? string.Empty,
                Nome = saida is null ? null : plan.NomesPorRotulo.GetValueOrDefault(saida.Label),
                Cpf = cpf,
                Resultado = ResultadosVinculacao.SoNoDp,
                CadastroDp = dp.Id,
                NomeDp = dp.Name?.Trim(),
                CodigoExternoDp = dp.ExternalId?.Trim(),
                CargoDp = CargoDp(dp.JobRole),
                Observacao = Join(SoNoDpMotivo(context, plan, cpf, saida, dp), SoNoDpVinculo(vinculo)),
            });
            total++;
        }

        if (total > 0)
        {
            context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.None, ItemStatuses.Warning,
                Message: FormattableString.Invariant(
                    $"so_no_dp: {total} cadastro(s) ativo(s) no Sólides DP sem colaborador ativo no RHSenso; a integração não mexe neles. Ver \"Só no Sólides DP\" no relatório de vinculação.")));
        }
    }

    /// <summary>O que o RHSenso diz do CPF de quem está ativo só no Sólides DP.</summary>
    private static string SoNoDpMotivo(SyncContext context, EmployeePlan plan, string? cpf, Departure? saida, EmployeeDto dp)
    {
        if (cpf is null)
        {
            return "CPF vazio ou inválido no Sólides DP: não dá para comparar com o RHSenso; corrigir o CPF no DP";
        }

        if (saida is not null)
        {
            var quando = saida.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var futuro = saida.Date > context.Today;
            return saida.IsTransfer
                ? $"transferido para outra empresa no RHSenso em {quando}; desligar nesta conta do Sólides DP" +
                  (futuro ? " nessa data" : string.Empty) + " (na conta da nova empresa a pessoa é vinculada pelo CPF)"
                : $"desligado no RHSenso em {quando} ({saida.Reason}); a integração só desliga quem ela vinculou: desligar no Sólides DP" +
                  (futuro ? " nessa data" : string.Empty);
        }

        if (EmployeeMapper.Clean(dp.ExternalId) is { } codigo && MatriculaComCodigo(plan, codigo) is { } matricula)
        {
            return FormattableString.Invariant(
                $"o Código Externo {codigo} é a matrícula {context.Cdempresa}-{matricula} do RHSenso, que tem outro CPF: conferir o CPF no Sólides DP ou no RHSenso");
        }

        return "CPF não aparece entre os colaboradores ativos do RHSenso nesta empresa (considerando os tipos de colaborador e as situações configurados): " +
               "conferir se a pessoa deve continuar no ponto";
    }

    /// <summary>Quem a integração já vinculou e agora aparece só no DP: CPF trocado no RHSenso ou readmitido no DP.</summary>
    private static string? SoNoDpVinculo(EntityState? vinculo) => vinculo switch
    {
        null => null,
        { Status: EntityStatuses.Dismissed } => "a integração já desligou este cadastro, mas ele está ativo no Sólides DP (readmitido lá?)",
        { } outro => $"vinculado pela integração à matrícula {outro.Matricula ?? "?"}: conferir se o CPF foi alterado no RHSenso",
    };

    /// <summary>Matrícula do RHSenso igual ao Código Externo do DP, também sem os zeros à esquerda ("7811" = "00007811").</summary>
    private static string? MatriculaComCodigo(EmployeePlan plan, string codigo)
    {
        if (plan.KeyByMatricula.ContainsKey(codigo))
        {
            return codigo;
        }

        if (!codigo.All(char.IsAsciiDigit))
        {
            return null;
        }

        var semZeros = codigo.TrimStart('0');
        return plan.KeyByMatricula.Keys.FirstOrDefault(m =>
            m.All(char.IsAsciiDigit) && string.Equals(m.TrimStart('0'), semZeros, StringComparison.Ordinal));
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
                var cargoNoDp = remote.JobRole?.ExternalId?.Trim();
                return linha with
                {
                    CadastroDp = remote.Id,
                    NomeDp = remote.Name?.Trim(),
                    CodigoExternoDp = remote.ExternalId?.Trim(),
                    CodigoExternoFica = CodigoExterno.ParaEnviar(remote.ExternalId, candidate.Row.Nomatric),
                    CargoDp = CargoDp(remote.JobRole),
                    CargoMuda = !string.Equals(cargoNoDp, candidate.Payload.JobRoleExternalId, StringComparison.Ordinal),
                };
            case DecisionKind.Create:
                return linha with
                {
                    CodigoExternoFica = matricula,
                    Observacao = context.CanQueryDp
                        ? candidate.Warnings
                        : Join("sem token: o Sólides DP não foi consultado; pode virar vínculo pelo CPF", candidate.Warnings),
                };
            default:
                // Já vinculado: o cadastro e o Código Externo são os guardados no vínculo.
                return linha with
                {
                    CadastroDp = candidate.Current?.RemoteId,
                    CodigoExternoDp = candidate.Current?.CodigoExterno,
                    CodigoExternoFica = candidate.Current?.CodigoExterno,
                };
        }
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
        var changes = new List<string>();
        if (CodigoExterno.DescreverMudanca(atual, CodigoExterno.ParaEnviar(atual, candidate.Row.Nomatric)) is { } codigo)
        {
            changes.Add(codigo);
        }

        if (decision.Remote?.JobRole is { } cargo
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
    private readonly HashSet<long> _ids = [];
    private readonly List<EmployeeDto> _todos = [];

    public int Count { get; private set; }

    /// <summary>Todos os ativos da conta, na ordem em que o DP devolveu.</summary>
    public IReadOnlyList<EmployeeDto> Todos => _todos;

    /// <summary>O cadastro (id do DP) está entre os ativos.</summary>
    public bool Contains(long id) => _ids.Contains(id);

    public static DpEmployeeIndex From(IEnumerable<EmployeeDto> employees)
    {
        var index = new DpEmployeeIndex();
        foreach (var employee in employees.Where(e => e.Fired != true))
        {
            index.Count++;
            index._ids.Add(employee.Id);
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
