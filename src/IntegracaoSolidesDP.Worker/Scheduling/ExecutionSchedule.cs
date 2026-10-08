using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>Calcula o próximo horário de execução a partir de Execution:Interval ou Execution:TimesOfDay.</summary>
public sealed class ExecutionSchedule(ExecutionOptions options, TimeZoneInfo zone)
{
    public bool RunOnStartup => options.Interval is not null && options.RunOnStartup;

    /// <summary>Próxima execução (UTC) depois de <paramref name="nowUtc"/>.</summary>
    public DateTimeOffset Next(DateTimeOffset nowUtc)
    {
        if (options.Interval is { } interval)
        {
            return nowUtc + interval;
        }

        var times = (options.TimesOfDay ?? []).Distinct().Order().ToList();
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);

        for (var dayOffset = 0; dayOffset <= 1; dayOffset++)
        {
            var day = local.Date.AddDays(dayOffset);
            foreach (var time in times)
            {
                var candidate = ToUtc(day + time);
                if (candidate > nowUtc)
                {
                    return candidate;
                }
            }
        }

        return ToUtc(local.Date.AddDays(2) + times[0]);
    }

    private DateTimeOffset ToUtc(DateTime localUnspecified)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        // Horário inexistente (início de horário de verão): empurra para depois do salto.
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
