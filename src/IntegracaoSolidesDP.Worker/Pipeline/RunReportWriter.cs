using System.Globalization;
using System.Text;
using System.Text.Json;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Resumo de uma execução: contagem por entidade e status.</summary>
public sealed record RunSummary(
    Guid RunId,
    string Status,
    bool DryRun,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counts,
    string? Error,
    string? ReportPath)
{
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
            var prefix = Path.Combine(directory, $"run-{stamp}-{(summary.DryRun ? "dryrun" : "real")}-{summary.RunId:N}");

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

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
