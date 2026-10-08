using System.Text.Json;
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
public sealed record RunRequest(string Trigger, bool? DryRunOverride, string? RequestedBy = null);

/// <summary>
/// Uma execução completa, na ordem de dependência:
/// referências → cargos → locais → desligamentos/transferências → colaboradores → férias.
/// </summary>
public sealed class SyncPipeline(
    ISourceReader source,
    IStateStore state,
    SyncSettingsLoader settingsLoader,
    SyncOptionsAccessor syncOptions,
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
        var instanceName = syncOptions.Configured.InstanceName;

        await state.EnsureSchemaAsync(ct);
        await using var runLock = await state.TryAcquireRunLockAsync(instanceName, ct);
        if (runLock is null)
        {
            logger.LogWarning("Outra execução da instância {Instance} está em andamento; esta foi ignorada.", instanceName);
            return NotRun(RunStatuses.SkippedLocked, request.DryRunOverride ?? syncOptions.Configured.DryRun, started, "Outra execução em andamento");
        }

        await state.VerifyKeySchemeAsync(EmployeeKey.Scheme, ct);

        // Com o lock na mão, nenhuma execução desta instância está viva: "running" é sobra de uma parada abrupta.
        var interrupted = await state.FailInterruptedRunsAsync(instanceName, ct);
        if (interrupted > 0)
        {
            logger.LogWarning("{Count} execução(ões) interrompida(s) anteriormente foram marcadas como failed", interrupted);
        }

        SyncSettings settings;
        try
        {
            settings = await settingsLoader.LoadAsync(ct);
        }
        catch (SyncAbortedException ex)
        {
            return await RecordInvalidConfigurationAsync(request, started, ex.Message, ct);
        }

        var dryRun = request.DryRunOverride ?? settings.Options.DryRun;
        if (!settings.Active && !dryRun)
        {
            logger.LogInformation("Integração desativada (configuração versão {Version}); execução {Trigger} não realizada.", settings.Version, request.Trigger);
            return NotRun(RunStatuses.SkippedDisabled, dryRun, started, "Integração desativada");
        }

        syncOptions.Use(settings.Options);
        var options = settings.Options;

        var runId = await state.StartRunAsync(instanceName, dryRun, request.Trigger, ct);
        await settingsLoader.TagRunAsync(runId, request.RequestedBy, settings, ct);
        var context = new SyncContext(runId, dryRun, dates.Today(clock));
        logger.LogInformation("Execução {RunId} iniciada ({Mode}, gatilho {Trigger}{User})",
            runId, dryRun ? "DRY-RUN" : "real", request.Trigger, request.RequestedBy is null ? string.Empty : $", pedida por {request.RequestedBy}");

        string status;
        string? error = null;
        try
        {
            var rows = await source.ReadEmployeesAsync(options.TiposColaborador.ToList(), options.EmpresasIncluidas.ToList(), ct);
            if (rows.Count == 0)
            {
                throw new SyncAbortedException("source_empty: nenhum colaborador lido do RHSenso; nada foi enviado (verifique a conexão e os filtros).");
            }

            var plan = EmployeeClassifier.Classify(rows, options, context.Today);
            context.AddRange(plan.Skipped);
            logger.LogInformation("RHSenso: {Active} colaboradores ativos em escopo, {Departures} saídas", plan.Active.Count, plan.Departures.Count);

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

        var finished = clock.GetUtcNow();
        var summary = new RunSummary(runId, status, dryRun, started, finished, RunSummary.CountItems(context.Items), error, null);

        // Mesmo cancelado (parada do serviço), o que foi feito fica registrado. Os itens "unchanged"
        // entram só na contagem (summary_json): gravá-los repetiria todo o cadastro a cada execução.
        await state.AddItemsAsync(runId, context.Items.Where(i => i.Status != ItemStatuses.Unchanged).ToList(), CancellationToken.None);
        var reportPath = await reports.WriteAsync(summary, context.Items, CancellationToken.None);
        summary = summary with { ReportPath = reportPath };
        await state.FinishRunAsync(runId, status, JsonSerializer.Serialize(summary.Counts), error, CancellationToken.None);

        logger.LogInformation("Execução {RunId} terminou: {Status} {Counts} relatório {Report}",
            runId, status, JsonSerializer.Serialize(summary.Counts), reportPath);
        ct.ThrowIfCancellationRequested();
        return summary;
    }

    /// <summary>Configuração do banco inválida: a execução fica registrada como failed, com o motivo, sem tocar no DP.</summary>
    private async Task<RunSummary> RecordInvalidConfigurationAsync(RunRequest request, DateTimeOffset started, string error, CancellationToken ct)
    {
        var dryRun = request.DryRunOverride ?? syncOptions.Configured.DryRun;
        var runId = await state.StartRunAsync(syncOptions.Configured.InstanceName, dryRun, request.Trigger, ct);
        await state.FinishRunAsync(runId, RunStatuses.Failed, "{}", error, CancellationToken.None);
        logger.LogError("Execução {RunId} abortada: {Error}", runId, error);
        return new RunSummary(runId, RunStatuses.Failed, dryRun, started, clock.GetUtcNow(),
            new Dictionary<string, IReadOnlyDictionary<string, int>>(), error, null);
    }

    private RunSummary NotRun(string status, bool dryRun, DateTimeOffset started, string reason) =>
        new(Guid.Empty, status, dryRun, started, clock.GetUtcNow(), new Dictionary<string, IReadOnlyDictionary<string, int>>(), reason, null);
}
