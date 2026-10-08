using System.Globalization;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Parâmetros do DataTables (server-side, GET) já validados.</summary>
public sealed record DataTablesRequest(int Draw, int Start, int Length, string? Search, int OrderColumn, bool OrderAscending)
{
    private const int MaxLength = 100;

    public static DataTablesRequest From(IQueryCollection query)
    {
        var search = query["search[value]"].ToString().Trim();
        return new DataTablesRequest(
            Draw: Int(query["draw"], 0),
            Start: Math.Max(Int(query["start"], 0), 0),
            Length: Math.Clamp(Int(query["length"], 25), 1, MaxLength),
            Search: search.Length == 0 ? null : search,
            OrderColumn: Math.Max(Int(query["order[0][column]"], 0), 0),
            OrderAscending: string.Equals(query["order[0][dir]"], "asc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Coluna de ordenação a partir de uma lista fechada (nunca texto do navegador no SQL).</summary>
    public string OrderBy(IReadOnlyList<string> columns) =>
        $"{columns[OrderColumn < columns.Count ? OrderColumn : 0]} {(OrderAscending ? "ASC" : "DESC")}";

    /// <summary>Texto da busca para LIKE, com %, _ e [ escapados (use ESCAPE '\').</summary>
    public string? Like => Search is null ? null : Sql.Like(Search);

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
}

/// <summary>Resposta no formato do DataTables.</summary>
public sealed record DataTablesResponse<T>(int Draw, int RecordsTotal, int RecordsFiltered, IReadOnlyList<T> Data);

public static class Sql
{
    /// <summary>Padrão "%texto%" para LIKE ... ESCAPE '\'.</summary>
    public static string Like(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal) + "%";
}
