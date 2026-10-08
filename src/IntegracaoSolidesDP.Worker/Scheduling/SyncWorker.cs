using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// Serviço (Windows Service / systemd): executa a sincronização na frequência configurada.
/// Com Gestao:Habilitada, entre uma execução e outra atende os comandos pedidos na Web.
/// </summary>
public sealed class SyncWorker(
    IServiceScopeFactory scopes,
    ExecutionSchedule schedule,
    CommandProcessor commands,
    IOptions<ManagementOptions> management,
    TimeProvider clock,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var managed = management.Value.Habilitada;
        if (managed)
        {
            await RecoverCommandsAsync(stoppingToken);
        }

        if (schedule.RunOnStartup)
        {
            await RunOnceAsync("startup", stoppingToken);
        }

        var next = NextRun();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (managed)
            {
                await ProcessCommandsAsync(stoppingToken);
            }

            var now = clock.GetUtcNow();
            if (now >= next)
            {
                await RunOnceAsync("schedule", stoppingToken);
                next = NextRun();
                continue;
            }

            var wait = next - now;
            if (managed && wait > management.Value.IntervaloComandos)
            {
                wait = management.Value.IntervaloComandos;
            }

            await Task.Delay(wait, clock, stoppingToken);
        }
    }

    private DateTimeOffset NextRun()
    {
        var next = schedule.Next(clock.GetUtcNow());
        logger.LogInformation("Próxima execução em {Next:yyyy-MM-dd HH:mm:ss} UTC", next);
        return next;
    }

    private async Task RunOnceAsync(string trigger, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SyncPipeline>().RunAsync(trigger, dryRunOverride: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Parada do serviço.
        }
        catch (Exception ex)
        {
            // Uma execução com erro (ex.: banco fora do ar) não derruba o serviço; a próxima tenta de novo.
            logger.LogError(ex, "Falha na execução agendada");
        }
    }

    private async Task RecoverCommandsAsync(CancellationToken ct)
    {
        try
        {
            await commands.RecoverAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Parada do serviço.
        }
        catch (Exception ex)
        {
            // Banco fora do ar na partida: a fila é tentada de novo no próximo ciclo.
            logger.LogError(ex, "Falha ao preparar a fila de comandos");
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        try
        {
            await commands.ProcessPendingAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Parada do serviço.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao ler a fila de comandos");
        }
    }
}
