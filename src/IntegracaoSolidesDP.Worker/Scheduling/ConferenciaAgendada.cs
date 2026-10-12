using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// Conferência semanal DP x RHSenso (Sync:ConferenciaAtiva, Sync:ConferenciaDiaSemana, Sync:ConferenciaHora), só com a
/// gestão ligada. A partir do dia e hora da semana, confere cada empresa habilitada que ainda não foi conferida desde
/// então (um pedido manual na mesma semana conta). Nunca lança: erro vai para o log e é tentado no ciclo seguinte.
/// </summary>
public sealed class ConferenciaAgendada(
    IServiceScopeFactory scopes,
    IManagementStore management,
    IOptions<SyncOptions> configured,
    IOptions<ManagementOptions> gestao,
    EpochDates dates,
    TimeProvider clock,
    ILogger<ConferenciaAgendada> logger)
{
    private DateTimeOffset? _feita;
    private DateTimeOffset _proximaTentativa = DateTimeOffset.MinValue;

    /// <summary>Roda a conferência da semana se já passou do dia e hora configurados e ela ainda não foi feita.</summary>
    public async Task ConferirSeDevidoAsync(CancellationToken ct)
    {
        if (!gestao.Value.Habilitada)
        {
            return;
        }

        var agora = clock.GetUtcNow();
        if (agora < _proximaTentativa)
        {
            return;
        }

        // Confere a configuração no máximo uma vez por minuto (o ciclo da gestão é de segundos).
        _proximaTentativa = agora.AddMinutes(1);

        try
        {
            var regras = await RegrasAsync(ct);
            if (!regras.ConferenciaAtiva)
            {
                return;
            }

            var marco = UltimoMarco(agora, regras.ConferenciaDiaSemana, regras.ConferenciaHora, dates.Zone);
            if (_feita == marco)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ConferenciaCampos>()
                .ExecutarAsync(cdempresa: null, GatilhosConferencia.Agendada, solicitadoPor: null, desde: marco, ct);
            _feita = marco;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Banco fora do ar, configuração ilegível: tenta de novo daqui a uma hora, sem encher o log a cada ciclo.
            _proximaTentativa = agora.AddHours(1);
            logger.LogError(ex, "Falha na conferência semanal DP x RHSenso; nova tentativa em uma hora");
        }
    }

    /// <summary>O dia e hora da conferência mais recente que já passou (no fuso do serviço), em UTC.</summary>
    public static DateTimeOffset UltimoMarco(DateTimeOffset agora, DayOfWeek dia, int hora, TimeZoneInfo fuso)
    {
        ArgumentNullException.ThrowIfNull(fuso);
        var local = TimeZoneInfo.ConvertTime(agora, fuso);
        var voltar = ((int)local.DayOfWeek - (int)dia + 7) % 7;
        var dataMarco = local.Date.AddDays(-voltar).AddHours(hora);
        if (dataMarco > local.DateTime)
        {
            dataMarco = dataMarco.AddDays(-7);
        }

        return new DateTimeOffset(dataMarco, fuso.GetUtcOffset(dataMarco)).ToUniversalTime();
    }

    private async Task<SyncOptions> RegrasAsync(CancellationToken ct)
    {
        var baseline = configured.Value;
        var atual = await management.GetCurrentConfigurationAsync(baseline.InstanceName, ct);
        return atual is null ? baseline : SyncOptionsJson.Deserialize(atual.SyncJson);
    }
}
