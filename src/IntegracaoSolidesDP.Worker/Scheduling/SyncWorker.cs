using IntegracaoSolidesDP.Worker.Pipeline;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>Serviço (Windows Service / systemd): executa a sincronização na frequência configurada.</summary>
public sealed class SyncWorker(
    IServiceScopeFactory scopes,
    ExecutionSchedule schedule,
    TimeProvider clock,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (schedule.RunOnStartup)
        {
            await RunOnceAsync("startup", stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = schedule.Next(clock.GetUtcNow());
            logger.LogInformation("Próxima execução em {Next:yyyy-MM-dd HH:mm:ss} UTC", next);
            var delay = next - clock.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, stoppingToken);
            }

            await RunOnceAsync("schedule", stoppingToken);
        }
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
}
