using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Mapping;

public enum VacationDecision
{
    Send,
    Skip,

    /// <summary>Reprogramada no RHSenso: o lançamento já enviado deve ser excluído no DP.</summary>
    Cancel,
}

public sealed record VacationMapping(
    VacationDecision Decision,
    string? Status,
    long StartDate,
    long EndDate,
    string? SkipReason,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Hash do que importa para o DP: período e status.</summary>
    public string Hash(long adjustmentReasonId, string employeeExternalId) =>
        PayloadHasher.Hash(new { adjustmentReasonId, employeeExternalId, StartDate, EndDate, Status });
}

public sealed class VacationMapper(EpochDates dates, SyncOptions options)
{
    public const string Origem = "Integração RHSenso";

    /// <summary>Marcador gravado em "observation": permite achar o lançamento no DP se a resposta do POST se perder.</summary>
    public static string Marker(Guid feria2Id) => $"RHSenso:{feria2Id:D}";

    public VacationMapping Map(VacationRow row)
    {
        var warnings = new List<string>();
        var first = DateOnly.FromDateTime(row.Inicio);
        var last = DateOnly.FromDateTime(row.Fim);
        var start = dates.StartOfDay(first);
        var end = options.FeriasEndDateMode switch
        {
            FeriasEndDateMode.InicioDoUltimoDia => dates.StartOfDay(last),
            FeriasEndDateMode.FimDoUltimoDia => dates.EndOfDay(last),
            _ => dates.StartOfDay(last.AddDays(1)),
        };

        if (last < first)
        {
            return new VacationMapping(VacationDecision.Skip, null, start, end, "invalid_period", warnings);
        }

        var calendarDays = last.DayNumber - first.DayNumber + 1;
        if (row.Dias is { } dias && dias != calendarDays)
        {
            // Abono pecuniário: os dias vendidos são trabalhados; o lançamento cobre só dtinipf..dtfimpf.
            warnings.Add($"days_mismatch:periodo={calendarDays},qtdiasfe={dias},abono={row.Abono ?? 0}");
        }

        return row.Situacao switch
        {
            2 or 3 or 4 or 6 => new VacationMapping(VacationDecision.Send, "APROVADO", start, end, null, warnings),
            1 when options.FeriasEnviarProgramadas => new VacationMapping(VacationDecision.Send, "PENDENTE", start, end, null, warnings),
            1 => new VacationMapping(VacationDecision.Skip, null, start, end, "programada", warnings),
            7 => new VacationMapping(VacationDecision.Cancel, null, start, end, "reprogramada", warnings),
            var other => new VacationMapping(VacationDecision.Skip, null, start, end, $"unknown_status:{other}", warnings),
        };
    }
}
