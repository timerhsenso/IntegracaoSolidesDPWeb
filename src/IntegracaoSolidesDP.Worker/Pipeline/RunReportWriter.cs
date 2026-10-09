using System.Globalization;
using System.Text;
using System.Text.Json;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Resumo de uma execução: contagem por entidade e status. Cada empresa é uma execução própria
/// (solidesdp.runs); um pedido que roda várias empresas devolve o resumo combinado, com as partes em <see cref="Empresas"/>.
/// </summary>
public sealed record RunSummary(
    Guid RunId,
    string Status,
    bool DryRun,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counts,
    string? Error,
    string? ReportPath,
    int? Cdempresa = null)
{
    /// <summary>Resumo de cada empresa (vazio quando o resumo já é de uma empresa só).</summary>
    public IReadOnlyList<RunSummary> Empresas { get; init; } = [];

    /// <summary>Junta os resumos das empresas de um pedido. Com uma empresa só, devolve o resumo dela.</summary>
    public static RunSummary Combine(IReadOnlyList<RunSummary> parts, bool dryRun, DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        var statuses = parts.Select(p => p.Status).ToList();
        var status = statuses switch
        {
            [] => RunStatuses.SkippedDisabled,
            _ when statuses.All(s => s == RunStatuses.Failed) => RunStatuses.Failed,
            _ when statuses.Any(s => s is RunStatuses.Failed or RunStatuses.CompletedWithErrors) => RunStatuses.CompletedWithErrors,
            _ when statuses.All(s => s == RunStatuses.SkippedLocked) => RunStatuses.SkippedLocked,
            _ when statuses.All(s => s is RunStatuses.SkippedLocked or RunStatuses.SkippedDisabled) => RunStatuses.SkippedDisabled,
            _ => RunStatuses.Completed,
        };

        var counts = parts
            .SelectMany(p => p.Counts.SelectMany(e => e.Value.Select(s => (Entity: e.Key, Status: s.Key, Count: s.Value))))
            .GroupBy(x => x.Entity, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g.GroupBy(x => x.Status, StringComparer.Ordinal)
                    .OrderBy(s => s.Key, StringComparer.Ordinal)
                    .ToDictionary(s => s.Key, s => s.Sum(x => x.Count), StringComparer.Ordinal),
                StringComparer.Ordinal);

        var errors = parts.Where(p => p.Error is not null).Select(p => p.Cdempresa is { } e ? FormattableString.Invariant($"empresa {e}: {p.Error}") : p.Error!).ToList();
        var reports = parts.Select(p => p.ReportPath).OfType<string>().ToList();
        var error = parts.Count == 0 ? "nenhuma empresa habilitada para a execução" : errors.Count == 0 ? null : string.Join(" | ", errors);
        return new RunSummary(Guid.Empty, status, dryRun, startedAt, finishedAt, counts, error,
            reports.Count == 0 ? null : string.Join(" | ", reports))
        {
            Empresas = parts,
        };
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> CountItems(IEnumerable<RunItem> items) =>
        items.GroupBy(i => i.EntityType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g.GroupBy(i => i.Status, StringComparer.Ordinal)
                    .OrderBy(s => s.Key, StringComparer.Ordinal)
                    .ToDictionary(s => s.Key, s => s.Count(), StringComparer.Ordinal),
                StringComparer.Ordinal);
}

/// <summary>Grava o relatório da execução (CSV com um item por linha + JSON com o resumo).</summary>
public sealed class RunReportWriter(IOptions<SyncOptions> options, ILogger<RunReportWriter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string?> WriteAsync(RunSummary summary, IReadOnlyList<RunItem> items, CancellationToken ct)
    {
        try
        {
            var directory = Path.IsPathRooted(options.Value.ReportDirectory)
                ? options.Value.ReportDirectory
                : Path.Combine(AppContext.BaseDirectory, options.Value.ReportDirectory);
            Directory.CreateDirectory(directory);

            var stamp = summary.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var prefix = Path.Combine(directory, $"run-{stamp}-empresa{summary.Cdempresa}-{(summary.DryRun ? "dryrun" : "real")}-{summary.RunId:N}");

            var csv = new StringBuilder("entidade;id_externo;acao;status;http;mensagem\n");
            foreach (var item in items)
            {
                csv.Append(Field(item.EntityType)).Append(';')
                    .Append(Field(item.ExternalId)).Append(';')
                    .Append(Field(item.Action)).Append(';')
                    .Append(Field(item.Status)).Append(';')
                    .Append(item.HttpStatus?.ToString(CultureInfo.InvariantCulture)).Append(';')
                    .Append(Field(item.Message)).Append('\n');
            }

            // BOM para o Excel abrir acentuação corretamente.
            await File.WriteAllTextAsync(prefix + ".csv", csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
            await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(summary with { ReportPath = prefix + ".csv" }, JsonOptions), ct);
            return prefix + ".csv";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Não foi possível gravar o relatório da execução {RunId}", summary.RunId);
            return null;
        }
    }

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // "15-1" (empresa-filial) o Excel abre como data (15/jan): vai como fórmula de texto.
        if (PareceData(value))
        {
            return "\"=\"\"" + value + "\"\"\"";
        }

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static bool PareceData(string value)
    {
        var traco = value.IndexOfAny(['-', '/']);
        return traco is > 0 and <= 4
               && value.Length - traco - 1 is > 0 and <= 4
               && value.Remove(traco, 1).All(char.IsAsciiDigit);
    }
}
