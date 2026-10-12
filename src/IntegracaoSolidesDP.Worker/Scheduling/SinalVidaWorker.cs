using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// Com Gestao:Habilitada, grava o sinal de vida do serviço (solidesdp.servico_sinal) a cada Gestao:IntervaloSinalDeVida,
/// também durante as execuções (roda à parte do <see cref="SyncWorker"/>). A Web mostra no Painel se o serviço está
/// respondendo. Erro de banco só vai para o log; o próximo ciclo tenta de novo.
/// </summary>
public sealed class SinalVidaWorker(
    ISinalServicoStore sinais,
    IManagementStore management,
    IOptions<ManagementOptions> gestao,
    IOptions<SyncOptions> sync,
    TimeProvider clock,
    ILogger<SinalVidaWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gestao.Value.Habilitada)
        {
            return;
        }

        var iniciadoEm = clock.GetUtcNow();
        var esquemaPronto = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!esquemaPronto)
                {
                    await management.EnsureSchemaAsync(stoppingToken);
                    esquemaPronto = true;
                }

                await sinais.RegistrarAsync(new SinalServico
                {
                    Instancia = sync.Value.InstanceName,
                    Maquina = Environment.MachineName,
                    Versao = VersaoServico.Atual,
                    Pid = Environment.ProcessId,
                    IniciadoEm = iniciadoEm,
                    UltimoSinal = clock.GetUtcNow(),
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Não foi possível gravar o sinal de vida do serviço");
            }

            try
            {
                await Task.Delay(gestao.Value.IntervaloSinalDeVida, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
