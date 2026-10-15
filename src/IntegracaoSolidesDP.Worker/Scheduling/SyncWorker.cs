using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// Serviço (Windows Service / systemd): executa a sincronização na agenda configurada (<see cref="AgendaServico"/>).
/// Com Gestao:Habilitada, entre uma execução e outra atende os comandos pedidos na Web e segue as mudanças da agenda
/// feitas nas regras gerais, sem reiniciar o serviço.
/// </summary>
public sealed class SyncWorker(
    IServiceScopeFactory scopes,
    ExecutionSchedule schedule,
    AgendaServico agenda,
    CommandProcessor commands,
    Notificador notificador,
    AvisoErros avisoErros,
    Limpeza limpeza,
    ConferenciaAgendada conferencia,
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

        var regime = await agenda.AtualAsync(stoppingToken);
        logger.LogInformation("Agenda das execuções: {Agenda}", regime.Descricao);
        DateTimeOffset? ultimaExecucao = null;
        if (schedule.RodarAoIniciar(regime, clock.GetUtcNow()))
        {
            await RunOnceAsync("startup", stoppingToken);
            ultimaExecucao = clock.GetUtcNow();
        }

        var next = NextRun(regime, ultimaExecucao);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (managed)
            {
                // Agenda alterada nas regras gerais: recalcula a próxima execução (o intervalo conta da última execução).
                var atual = await agenda.AtualAsync(stoppingToken);
                if (!string.Equals(atual.Descricao, regime.Descricao, StringComparison.Ordinal))
                {
                    logger.LogInformation("Agenda das execuções alterada: {Antes} → {Depois}", regime.Descricao, atual.Descricao);
                    regime = atual;
                    next = NextRun(regime, ultimaExecucao);
                }

                await ProcessCommandsAsync(stoppingToken);
                // Resumo diário das pendências: confere a hora a cada ciclo; envia uma vez por dia.
                await notificador.ResumoSeDevidoAsync(stoppingToken);
                // Erros da aplicação ao suporte: pedidos do botão, primeiro aviso e resumo (conforme o modo de envio).
                await avisoErros.ProcessarAsync(stoppingToken);
                // Conferência semanal DP x RHSenso: a partir do dia e hora configurados.
                await conferencia.ConferirSeDevidoAsync(stoppingToken);
            }

            // Limpeza do histórico antigo: uma vez por dia, a partir de Sync:LimpezaHora.
            await limpeza.LimparSeDevidoAsync(stoppingToken);

            var now = clock.GetUtcNow();
            if (now >= next)
            {
                await RunOnceAsync("schedule", stoppingToken);
                ultimaExecucao = clock.GetUtcNow();
                next = NextRun(regime, ultimaExecucao);
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

    private DateTimeOffset NextRun(RegimeExecucao regime, DateTimeOffset? ultimaExecucao)
    {
        var next = schedule.Proxima(regime, clock.GetUtcNow(), ultimaExecucao);
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
