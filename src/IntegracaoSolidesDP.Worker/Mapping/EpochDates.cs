namespace IntegracaoSolidesDP.Worker.Mapping;

/// <summary>
/// O Sólides DP representa datas em epoch milissegundos. As datas do RHSenso são
/// "datas de calendário" (meia-noite, sem fuso): são interpretadas como meia-noite
/// local no fuso configurado (Execution:TimeZone) antes da conversão.
/// </summary>
public sealed class EpochDates(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    public long StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    public long StartOfDay(DateTime date) => StartOfDay(DateOnly.FromDateTime(date));

    public long? StartOfDay(DateTime? date) => date is { } d ? StartOfDay(d) : null;

    public long EndOfDay(DateOnly date) => StartOfDay(date.AddDays(1)) - 1;

    /// <summary>Dia (no fuso configurado) de uma data em epoch ms vinda do Sólides DP.</summary>
    public DateOnly ToDate(long epochMillis) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMillis), Zone).DateTime);

    public DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);
}
