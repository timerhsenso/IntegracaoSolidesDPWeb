using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// Grava em solidesdp.erro_aplicacao os erros que o <see cref="CapturaErrosSink"/> pôs na fila. Só com Gestao:Habilitada
/// (sem a gestão as tabelas não existem). Erro do banco vai para o log como aviso (não vira erro registrado: evita laço).
/// </summary>
public sealed class GravadorErros(
    FilaErros fila,
    IErroStore store,
    IOptions<ManagementOptions> gestao,
    TimeProvider clock,
    ILogger<GravadorErros> logger) : BackgroundService
{
    private static readonly TimeSpan PausaAposFalha = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gestao.Value.Habilitada)
        {
            fila.Encerrar();
            return;
        }

        try
        {
            while (await fila.AguardarAsync(stoppingToken))
            {
                var resultado = await fila.GravarAsync(store, clock.GetUtcNow(), stoppingToken);
                if (resultado.Falhas > 0)
                {
                    logger.LogWarning(resultado.UltimaFalha,
                        "{Falhas} erro(s) da aplicação não foram gravados no banco (continuam no arquivo de log)", resultado.Falhas);
                    await Task.Delay(PausaAposFalha, clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada do serviço: o que restou na fila tenta ser gravado no StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!gestao.Value.Habilitada)
        {
            return;
        }

        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limite.CancelAfter(TimeSpan.FromSeconds(5));
            await fila.GravarAsync(store, clock.GetUtcNow(), limite.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível gravar os últimos erros da aplicação na parada do serviço");
        }
    }
}
