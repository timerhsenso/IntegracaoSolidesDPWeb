using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// A agenda das execuções automáticas em vigor. Com a gestão ligada, vem das regras gerais (Sync:AgendaModo) da versão
/// vigente da configuração, relida no máximo uma vez por minuto; se lá estiver "como no arquivo do serviço", ou com a
/// gestão desligada, vale Execution:Interval/TimesOfDay do appsettings.json. Nunca lança: se o banco não responder,
/// segue com a última agenda lida.
/// </summary>
public sealed class AgendaServico(
    ExecutionSchedule arquivo,
    IManagementStore management,
    IOptions<SyncOptions> configured,
    IOptions<ManagementOptions> gestao,
    TimeProvider clock,
    ILogger<AgendaServico> logger)
{
    private RegimeExecucao? _atual;
    private DateTimeOffset _proximaLeitura = DateTimeOffset.MinValue;
    private bool _falhaAvisada;

    /// <summary>A agenda em vigor agora.</summary>
    public async Task<RegimeExecucao> AtualAsync(CancellationToken ct)
    {
        if (!gestao.Value.Habilitada)
        {
            return arquivo.Regime;
        }

        var agora = clock.GetUtcNow();
        if (_atual is not null && agora < _proximaLeitura)
        {
            return _atual;
        }

        _proximaLeitura = agora.AddMinutes(1);
        try
        {
            var baseline = configured.Value;
            var versao = await management.GetCurrentConfigurationAsync(baseline.InstanceName, ct);
            var regras = versao is null ? baseline : SyncOptionsJson.Deserialize(versao.SyncJson);
            _atual = RegimeExecucao.DaConfiguracao(regras) ?? arquivo.Regime;
            _falhaAvisada = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_falhaAvisada)
            {
                logger.LogWarning(ex, "Não foi possível ler a agenda das execuções nas regras gerais; segue a agenda atual");
                _falhaAvisada = true;
            }

            _atual ??= arquivo.Regime;
        }

        return _atual;
    }
}
