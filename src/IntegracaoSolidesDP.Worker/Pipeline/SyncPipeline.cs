using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Pipeline.Steps;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Pedido de execução.</summary>
/// <param name="Trigger">Origem: startup, schedule, cli, cli-dry-run, web...</param>
/// <param name="DryRunOverride">true força o dry-run; null segue a configuração.</param>
/// <param name="RequestedBy">Usuário da Web que pediu (null nas execuções do próprio serviço).</param>
/// <param name="Cdempresa">Só esta empresa; null = todas as empresas habilitadas.</param>
public sealed record RunRequest(string Trigger, bool? DryRunOverride, string? RequestedBy = null, int? Cdempresa = null);

/// <summary>
/// Uma execução por empresa habilitada (cada empresa é uma conta do Sólides DP, com o seu token), uma
/// empresa depois da outra. Em cada empresa, na ordem de dependência:
/// referências → cargos → locais → desligamentos/transferências → colaboradores → férias.
/// </summary>
public sealed class SyncPipeline(
    ISourceReader source,
    IStateStore state,
    SyncSettingsLoader settingsLoader,
    SyncOptionsAccessor syncOptions,
    SolidesDpAccount account,
    ReferenceResolver references,
    JobRoleStep jobRoles,
    WorkplaceStep workplaces,
    DismissalStep dismissals,
    EmployeeStep employees,
    VacationStep vacations,
    RunReportWriter reports,
    EpochDates dates,
    TimeProvider clock,
    ILogger<SyncPipeline> logger)
{
    public Task<RunSummary> RunAsync(string trigger, bool? dryRunOverride, CancellationToken ct) =>
        RunAsync(new RunRequest(trigger, dryRunOverride), ct);

    public async Task<RunSummary> RunAsync(RunRequest request, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var forcedDryRun = request.DryRunOverride == true;

        await state.EnsureSchemaAsync(ct);
        await state.VerifyKeySchemeAsync(EmployeeKey.Scheme, ct);

        SyncSettings settings;
        try
        {
            settings = await settingsLoader.LoadAsync(ct);
        }
        catch (SyncAbortedException ex)
        {
            return await RecordFailureAsync(request, request.Cdempresa, forcedDryRun || syncOptions.Configured.DryRun, started, ex.Message, ct);
        }

        var selected = settings.Empresas
            .Where(e => request.Cdempresa is null || e.Cdempresa == request.Cdempresa)
            .ToList();
        if (selected.Count == 0)
        {
            var reason = request.Cdempresa is { } pedida
                ? FormattableString.Invariant($"A empresa {pedida} não está habilitada na integração")
                : "Nenhuma empresa habilitada na integração";
            logger.LogInformation("{Reason}; execução {Trigger} não realizada.", reason, request.Trigger);
            return NotRun(RunStatuses.SkippedDisabled, forcedDryRun || settings.Options.DryRun, started, reason, request.Cdempresa);
        }

        // Empresa inativa na folha (temp1.flativo) fica fora do escopo: não envia nem desliga ninguém.
        var ativas = (await source.ReadEmpresasAtivasAsync(ct)).Select(e => e.Cdempresa).ToHashSet();
        var parts = new List<RunSummary>();
        foreach (var empresa in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!ativas.Contains(empresa.Cdempresa))
            {
                logger.LogWarning("Empresa {Empresa} está inativa no RHSenso (temp1.flativo); fora do escopo, nada foi enviado.", empresa.Cdempresa);
                parts.Add(NotRun(RunStatuses.SkippedDisabled, forcedDryRun || empresa.Options.DryRun, clock.GetUtcNow(),
                    "empresa inativa no RHSenso (temp1.flativo): fora do escopo, nada é enviado nem desligado", empresa.Cdempresa));
                continue;
            }

            parts.Add(await RunEmpresaAsync(request, settings, empresa, ct));
        }

        return RunSummary.Combine(parts, forcedDryRun || parts.All(p => p.DryRun), started, clock.GetUtcNow());
    }

    private async Task<RunSummary> RunEmpresaAsync(RunRequest request, SyncSettings settings, EmpresaSettings empresa, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var instanceName = syncOptions.Configured.InstanceName;
        var options = empresa.Options;
        var dryRun = request.DryRunOverride == true || options.DryRun;

        await using var runLock = await state.TryAcquireRunLockAsync(instanceName, empresa.Cdempresa, ct);
        if (runLock is null)
        {
            logger.LogWarning("Outra execução da empresa {Empresa} está em andamento; esta foi ignorada.", empresa.Cdempresa);
            return NotRun(RunStatuses.SkippedLocked, dryRun, started, "Outra execução em andamento", empresa.Cdempresa);
        }

        // Com o lock na mão, nenhuma execução desta empresa está viva: "running" é sobra de uma parada abrupta.
        var interrupted = await state.FailInterruptedRunsAsync(instanceName, empresa.Cdempresa, ct);
        if (interrupted > 0)
        {
            logger.LogWarning("{Count} execução(ões) interrompida(s) anteriormente foram marcadas como failed", interrupted);
        }

        if (!settings.Active && !dryRun)
        {
            logger.LogInformation("Integração desativada (configuração versão {Version}); execução {Trigger} da empresa {Empresa} não realizada.",
                settings.Version, request.Trigger, empresa.Cdempresa);
            return NotRun(RunStatuses.SkippedDisabled, dryRun, started, "Integração desativada", empresa.Cdempresa);
        }

        if (!dryRun && empresa.Problema is { } problema)
        {
            return await RecordFailureAsync(request, empresa.Cdempresa, dryRun, started, problema, ct);
        }

        syncOptions.Use(options);
        if (empresa.Token is { } token)
        {
            account.Use(empresa.Cdempresa, token);
        }
        else
        {
            account.Clear();
        }

        var runId = await state.StartRunAsync(instanceName, empresa.Cdempresa, dryRun, request.Trigger, ct);
        await settingsLoader.TagRunAsync(runId, request.RequestedBy, settings, ct);
        var context = new SyncContext(runId, empresa.Cdempresa, dryRun, dates.Today(clock)) { CanQueryDp = !dryRun || empresa.Token is not null };
        logger.LogInformation("Execução {RunId} da empresa {Empresa} iniciada ({Mode}, gatilho {Trigger}{User})",
            runId, empresa.Cdempresa, dryRun ? "DRY-RUN" : "real", request.Trigger,
            request.RequestedBy is null ? string.Empty : $", pedida por {request.RequestedBy}");

        string status;
        string? error = null;
        try
        {
            var escopo = await EscopoAsync(empresa, ct);
            var rows = await source.ReadEmployeesAsync(options.TiposColaborador.ToList(), [empresa.Cdempresa], ct);
            if (rows.Count == 0)
            {
                throw new SyncAbortedException(FormattableString.Invariant(
                    $"source_empty: nenhum colaborador da empresa {empresa.Cdempresa} lido do RHSenso; nada foi enviado (verifique a conexão e os filtros)."));
            }

            var plan = EmployeeClassifier.Classify(rows, options, context.Today, escopo);
            context.AddRange(plan.Skipped);
            logger.LogInformation(
                "RHSenso, empresa {Empresa}: {Active} colaboradores ativos em escopo, {OutOfScope} fora das filiais marcadas, {Departures} saídas",
                empresa.Cdempresa, plan.Active.Count, plan.OutOfScope.Count, plan.Departures.Count);

            if (!dryRun)
            {
                await references.ResolveAsync(context, plan, ct);
            }

            await jobRoles.ExecuteAsync(context, plan, ct);
            await workplaces.ExecuteAsync(context, plan, ct);
            await dismissals.ExecuteAsync(context, plan, ct);
            await employees.ExecuteAsync(context, plan, ct);
            await vacations.ExecuteAsync(context, plan, ct);

            status = context.HasErrors ? RunStatuses.CompletedWithErrors : RunStatuses.Completed;
        }
        catch (SyncAbortedException ex)
        {
            status = RunStatuses.Failed;
            error = ex.Message;
            logger.LogError("Execução {RunId} abortada: {Error}", runId, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = RunStatuses.Failed;
            error = "cancelled: o serviço foi parado durante a execução";
            logger.LogWarning("Execução {RunId} interrompida pela parada do serviço", runId);
        }
        catch (Exception ex)
        {
            status = RunStatuses.Failed;
            error = ex.ToString();
            logger.LogError(ex, "Execução {RunId} falhou", runId);
        }
        finally
        {
            account.Clear();
        }

        var finished = clock.GetUtcNow();
        var summary = new RunSummary(runId, status, dryRun, started, finished, RunSummary.CountItems(context.Items), error, null, empresa.Cdempresa);

        // Mesmo cancelado (parada do serviço), o que foi feito fica registrado. Os itens "unchanged"
        // entram só na contagem (summary_json): gravá-los repetiria todo o cadastro a cada execução.
        await state.AddItemsAsync(runId, context.Items.Where(i => i.Status != ItemStatuses.Unchanged).ToList(), CancellationToken.None);
        var reportPath = await reports.WriteAsync(summary, context.Items, CancellationToken.None);
        summary = summary with { ReportPath = reportPath };
        await state.FinishRunAsync(runId, status, JsonSerializer.Serialize(summary.Counts), error, CancellationToken.None);

        logger.LogInformation("Execução {RunId} da empresa {Empresa} terminou: {Status} {Counts} relatório {Report}",
            runId, empresa.Cdempresa, status, JsonSerializer.Serialize(summary.Counts), reportPath);
        ct.ThrowIfCancellationRequested();
        return summary;
    }

    /// <summary>
    /// Filiais da execução: as marcadas (ou todas, se nenhuma foi marcada) que estão ativas no RHSenso
    /// (test1.flativofilial = 1). Colaborador ativo fora delas fica fora do escopo: nem envia nem desliga.
    /// </summary>
    private async Task<EscopoEmpresa> EscopoAsync(EmpresaSettings empresa, CancellationToken ct)
    {
        var ativas = (await source.ReadWorkplacesAsync(ct))
            .Where(w => w.Cdempresa == empresa.Cdempresa && w.Ativa)
            .Select(w => w.Cdfilial)
            .ToHashSet();
        var filiais = empresa.Filiais.Count == 0 ? ativas : empresa.Filiais.Where(ativas.Contains).ToHashSet();
        if (filiais.Count == 0)
        {
            throw new SyncAbortedException(empresa.Filiais.Count == 0
                ? FormattableString.Invariant(
                    $"sem_filial_ativa: a empresa {empresa.Cdempresa} não tem filial ativa no RHSenso (test1.flativofilial = 1); nada foi enviado.")
                : FormattableString.Invariant(
                    $"sem_filial_ativa: nenhuma das filiais marcadas da empresa {empresa.Cdempresa} ({string.Join(", ", empresa.Filiais.Order())}) está ativa no RHSenso; nada foi enviado."));
        }

        return new EscopoEmpresa(empresa.Cdempresa, filiais);
    }

    /// <summary>Configuração inválida: a execução fica registrada como failed, com o motivo, sem tocar no DP.</summary>
    private async Task<RunSummary> RecordFailureAsync(
        RunRequest request, int? cdempresa, bool dryRun, DateTimeOffset started, string error, CancellationToken ct)
    {
        var runId = await state.StartRunAsync(syncOptions.Configured.InstanceName, cdempresa, dryRun, request.Trigger, ct);
        await state.FinishRunAsync(runId, RunStatuses.Failed, "{}", error, CancellationToken.None);
        logger.LogError("Execução {RunId} abortada: {Error}", runId, error);
        return new RunSummary(runId, RunStatuses.Failed, dryRun, started, clock.GetUtcNow(),
            new Dictionary<string, IReadOnlyDictionary<string, int>>(), error, null, cdempresa);
    }

    private RunSummary NotRun(string status, bool dryRun, DateTimeOffset started, string reason, int? cdempresa) =>
        new(Guid.Empty, status, dryRun, started, clock.GetUtcNow(), new Dictionary<string, IReadOnlyDictionary<string, int>>(), reason, null, cdempresa);
}
