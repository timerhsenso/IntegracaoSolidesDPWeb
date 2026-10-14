using System.Globalization;

namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>De onde vem a agenda das execuções automáticas do serviço.</summary>
public enum AgendaModo
{
    /// <summary>Como no arquivo de configuração do serviço (Execution:Interval ou Execution:TimesOfDay).</summary>
    ArquivoDoServico,

    /// <summary>A cada <see cref="SyncOptions.AgendaIntervaloMinutos"/> minutos, nos dias marcados.</summary>
    Intervalo,

    /// <summary>Nos horários de <see cref="SyncOptions.AgendaHorarios"/>, nos dias marcados.</summary>
    Horarios,
}

/// <summary>Leitura dos horários da agenda ("8", "8h", "8:00", "08:00", "13:30").</summary>
public static class HorarioAgenda
{
    private static readonly string[] Formatos = ["%H", "HH", "H'h'", "HH'h'", "H:mm", "HH:mm", "H'h'mm", "HH'h'mm"];

    /// <summary>Lê um horário do dia; devolve false para texto que não é um horário de 00:00 a 23:59.</summary>
    public static bool TentarLer(string? texto, out TimeOnly horario) =>
        TimeOnly.TryParseExact((texto ?? string.Empty).Trim().ToLowerInvariant(), Formatos, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out horario);

    /// <summary>O horário no formato gravado na configuração ("08:00").</summary>
    public static string Formatar(TimeOnly horario) => horario.ToString("HH:mm", CultureInfo.InvariantCulture);
}
