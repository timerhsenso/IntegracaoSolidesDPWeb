using System.Globalization;
using System.Text.Json.Nodes;

namespace SolidesDP.Fake.Http;

/// <summary>
/// Paginacao no estilo Spring Data: aceita <c>page/size</c> e <c>pageNumber/pageSize</c> (base 0); valores invalidos
/// caem no padrao (como o <c>PageableHandlerMethodArgumentResolver</c>). <c>offset</c> e aceito e ignorado.
/// </summary>
internal readonly record struct PageRequest(int Number, int Size)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 1000;

    public static PageRequest From(IQueryCollection query)
    {
        var number = ParseInt(query, "page") ?? ParseInt(query, "pageNumber") ?? 0;
        var size = ParseInt(query, "size") ?? ParseInt(query, "pageSize") ?? DefaultSize;
        return new PageRequest(Math.Max(number, 0), size < 1 ? DefaultSize : Math.Min(size, MaxSize));
    }

    /// <summary>Monta o <c>Page</c> do Spring: content, first, last, number, numberOfElements, size, sort, totalElements, totalPages.</summary>
    public JsonObject ToPage<T>(IReadOnlyList<T> items, Func<T, JsonNode> project)
    {
        var total = items.Count;
        var content = new JsonArray();
        foreach (var item in items.Skip((int)Math.Min((long)Number * Size, int.MaxValue)).Take(Size))
        {
            content.Add(project(item));
        }

        var totalPages = (int)Math.Ceiling(total / (double)Size);
        return new JsonObject
        {
            ["content"] = content,
            ["first"] = Number == 0,
            ["last"] = Number >= totalPages - 1,
            ["number"] = Number,
            ["numberOfElements"] = content.Count,
            ["size"] = Size,
            ["sort"] = new JsonObject { ["sorted"] = false, ["unsorted"] = true, ["empty"] = true },
            ["totalElements"] = total,
            ["totalPages"] = totalPages,
        };
    }

    private static int? ParseInt(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values)
        && int.TryParse(values.LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
