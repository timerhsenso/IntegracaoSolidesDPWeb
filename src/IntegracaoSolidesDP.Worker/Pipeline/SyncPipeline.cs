using System.Text.Json;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline.Steps;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Uma execução completa, na ordem de dependência:
/// referências → cargos → locais → desligamentos/transferências → colaboradores → férias.
/// </summary>
public sealed class SyncPipeline(
    ISourceReader source,
    IStateStore state,
    ReferenceResolver references,
    JobRoleStep jobRoles,
    WorkplaceStep workplaces,
    DismissalStep dismissals,
    EmployeeStep employees,
    VacationStep vacations,
    RunReportWriter reports,
    EpochDates dates,
    TimeProvider clock,
    IOptions<SyncOptions> syncOptions,
    ILogger<SyncPipeline> logger)
{
    private SyncOptions Options => syncOptions.Value;

    public async Task<RunSummary> RunAsync(string trigger, bool? dryRunOverride, CancellationToken ct)
    {
        var dryRun = dryRunOverride ?? Options.DryRun;
        var started = clock.GetUtcNow();

        await state.EnsureSchemaAsync(ct);
        await using var runLock = await state.TryAcquireRunLockAsync(Options.InstanceName, ct);
        if (runLock is null)
        {
            logger.LogWarning("Outra execução da instância {Instance} está em andamento; esta foi ignorada.", Options.InstanceName);
            return new RunSummary(Guid.Empty, "skipped_locked", dryRun, started, clock.GetUtcNow(),
                new Dictionary<string, IReadOnlyDictionary<string, int>>(), "Outra execução em andamento", null);
        }

        await state.VerifyKeySchemeAsync(EmployeeKey.Scheme, ct);
        var runId = await state.StartRunAsync(Options.InstanceName, dryRun, trigger, ct);
        var context = new SyncContext(runId, dryRun, dates.Today(clock));
        logger.LogInformation("Execução {RunId} iniciada ({Mode}, gatilho {Trigger})", runId, dryRun ? "DRY-RUN" : "real", trigger);

        string status;
        string? error = null;
        try
        {
            var rows = await source.ReadEmployeesAsync(Options.TiposColaborador.ToList(), Options.EmpresasIncluidas.ToList(), ct);
            if (rows.Count == 0)
            {
                throw new SyncAbortedException("source_empty: nenhum colaborador lido do RHSenso; nada foi enviado (verifique a conexão e os filtros).");
            }

            var plan = EmployeeClassifier.Classify(rows, Options, context.Today);
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

        // Mesmo cancelado (parada do serviço), o que foi feito fica registrado.
        await state.AddItemsAsync(runId, context.Items.ToList(), CancellationToken.None);
        var reportPath = await reports.WriteAsync(summary, context.Items, CancellationToken.None);
        summary = summary with { ReportPath = reportPath };
        await state.FinishRunAsync(runId, status, JsonSerializer.Serialize(summary.Counts), error, CancellationToken.None);

        logger.LogInformation("Execução {RunId} terminou: {Status} {Counts} relatório {Report}",
            runId, status, JsonSerializer.Serialize(summary.Counts), reportPath);
        ct.ThrowIfCancellationRequested();
        return summary;
    }
}
