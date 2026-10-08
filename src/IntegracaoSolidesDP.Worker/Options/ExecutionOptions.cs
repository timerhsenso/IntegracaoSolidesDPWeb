namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>
/// Frequência de execução do worker. Informe <see cref="Interval"/> (ex.: "00:30:00")
/// ou <see cref="TimesOfDay"/> (ex.: ["07:00", "13:00"]), nunca os dois.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    public TimeSpan? Interval { get; set; }

    public IList<TimeSpan>? TimesOfDay { get; set; }

    /// <summary>Fuso (IANA) em que os horários de <see cref="TimesOfDay"/> e as datas do RHSenso são interpretados.</summary>
    public string TimeZone { get; set; } = "America/Bahia";

    /// <summary>Com <see cref="Interval"/>, executa imediatamente ao iniciar o serviço.</summary>
    public bool RunOnStartup { get; set; } = true;
}
