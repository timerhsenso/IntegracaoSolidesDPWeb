using System.Globalization;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>
/// Agenda das execuções automáticas já resolvida: a do arquivo do serviço (Execution:Interval ou Execution:TimesOfDay)
/// ou a das regras gerais da Web (Sync:AgendaModo). Ou intervalo, ou horários; os dias da semana valem para os dois.
/// </summary>
public sealed record RegimeExecucao
{
    private static readonly DayOfWeek[] Semana =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public TimeSpan? Intervalo { get; init; }

    /// <summary>Horários locais do dia, em ordem e sem repetição.</summary>
    public IReadOnlyList<TimeSpan> Horarios { get; init; } = [];

    /// <summary>Dias com execução automática, na ordem de segunda a domingo.</summary>
    public IReadOnlyList<DayOfWeek> Dias { get; init; } = Semana;

    /// <summary>Veio das regras gerais da Web (e não do arquivo do serviço).</summary>
    public bool DaWeb { get; init; }

    /// <summary>Texto da agenda para o log; duas agendas com a mesma descrição são a mesma agenda.</summary>
    public string Descricao
    {
        get
        {
            var quando = Intervalo is { } intervalo
                ? "a cada " + (intervalo.TotalMinutes % 1 == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{(int)intervalo.TotalMinutes} min")
                    : intervalo.ToString("c", CultureInfo.InvariantCulture))
                : "às " + string.Join(", ", Horarios.Select(h => h.ToString(@"hh\:mm", CultureInfo.InvariantCulture)));
            var dias = Dias.Count == Semana.Length ? "todos os dias" : string.Join(", ", Dias.Select(d => PtBr.DateTimeFormat.GetDayName(d)));
            return $"{quando}, {dias} ({(DaWeb ? "regras gerais da Web" : "arquivo do serviço")})";
        }
    }

    /// <summary>A agenda do appsettings.json (Execution), todos os dias.</summary>
    public static RegimeExecucao DoArquivo(ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RegimeExecucao
        {
            Intervalo = options.Interval,
            Horarios = options.Interval is null ? (options.TimesOfDay ?? []).Distinct().Order().ToList() : [],
        };
    }

    /// <summary>
    /// A agenda das regras gerais; null quando elas mandam seguir o arquivo do serviço ou não têm o que agendar
    /// (versão inválida gravada por fora da Web).
    /// </summary>
    public static RegimeExecucao? DaConfiguracao(SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dias = Semana.Where(options.AgendaDiasSemana.Contains).ToList();
        if (dias.Count == 0)
        {
            dias = [.. Semana];
        }

        switch (options.AgendaModo)
        {
            case AgendaModo.Intervalo when options.AgendaIntervaloMinutos > 0:
                return new RegimeExecucao { Intervalo = TimeSpan.FromMinutes(options.AgendaIntervaloMinutos), Dias = dias, DaWeb = true };
            case AgendaModo.Horarios:
                var horarios = options.AgendaHorarios
                    .Select(h => HorarioAgenda.TentarLer(h, out var horario) ? horario.ToTimeSpan() : (TimeSpan?)null)
                    .OfType<TimeSpan>()
                    .Distinct()
                    .Order()
                    .ToList();
                return horarios.Count == 0 ? null : new RegimeExecucao { Horarios = horarios, Dias = dias, DaWeb = true };
            default:
                return null;
        }
    }
}

/// <summary>
/// Calcula o próximo horário de execução. A agenda do arquivo do serviço (Execution) é a padrão; a das regras gerais da
/// Web chega por <see cref="AgendaServico"/>.
/// </summary>
public sealed class ExecutionSchedule(ExecutionOptions options, TimeZoneInfo zone)
{
    /// <summary>A agenda do arquivo do serviço.</summary>
    public RegimeExecucao Regime { get; } = RegimeExecucao.DoArquivo(options);

    public bool RunOnStartup => Regime.Intervalo is not null && options.RunOnStartup;

    /// <summary>
    /// Com intervalo (do arquivo ou da Web) e Execution:RunOnStartup, executa logo que o serviço inicia, se hoje for um
    /// dos dias marcados.
    /// </summary>
    public bool RodarAoIniciar(RegimeExecucao regime, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(regime);
        return regime.Intervalo is not null && options.RunOnStartup
               && regime.Dias.Contains(TimeZoneInfo.ConvertTime(nowUtc, zone).DayOfWeek);
    }

    /// <summary>Próxima execução (UTC) depois de <paramref name="nowUtc"/>, pela agenda do arquivo.</summary>
    public DateTimeOffset Next(DateTimeOffset nowUtc) => Proxima(Regime, nowUtc);

    /// <summary>
    /// Próxima execução (UTC) de <paramref name="regime"/>. Com intervalo, conta a partir de <paramref name="desde"/> (o fim
    /// da última execução; sem ele, de agora) e nunca antes de agora; fora dos dias marcados, vai para a 00:00 do próximo dia
    /// marcado. Com horários, é o primeiro horário depois de agora num dia marcado.
    /// </summary>
    public DateTimeOffset Proxima(RegimeExecucao regime, DateTimeOffset nowUtc, DateTimeOffset? desde = null)
    {
        ArgumentNullException.ThrowIfNull(regime);
        var dias = regime.Dias.ToHashSet();

        if (regime.Intervalo is { } interval)
        {
            var candidata = (desde ?? nowUtc) + interval;
            if (candidata < nowUtc)
            {
                candidata = nowUtc;
            }

            var local = TimeZoneInfo.ConvertTime(candidata, zone);
            if (dias.Contains(local.DayOfWeek))
            {
                return candidata;
            }

            for (var dayOffset = 1; dayOffset <= 7; dayOffset++)
            {
                var dia = local.Date.AddDays(dayOffset);
                if (dias.Contains(dia.DayOfWeek))
                {
                    return ToUtc(dia);
                }
            }

            return candidata;
        }

        var agora = TimeZoneInfo.ConvertTime(nowUtc, zone);
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var day = agora.Date.AddDays(dayOffset);
            if (!dias.Contains(day.DayOfWeek))
            {
                continue;
            }

            foreach (var time in regime.Horarios)
            {
                var candidate = ToUtc(day + time);
                if (candidate > nowUtc)
                {
                    return candidate;
                }
            }
        }

        // Sem horário nenhum (os validadores exigem pelo menos um): tenta de novo amanhã.
        return nowUtc.AddDays(1);
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
