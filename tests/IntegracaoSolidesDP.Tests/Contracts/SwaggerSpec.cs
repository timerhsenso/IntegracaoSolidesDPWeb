using System.Text.Json;

namespace IntegracaoSolidesDP.Tests.Contracts;

/// <summary>Leitura mínima do Swagger 2.0 do Sólides DP (spec/tangerino-employer.json).</summary>
public sealed class SwaggerSpec
{
    private static readonly Lazy<SwaggerSpec> Instance = new(() => new SwaggerSpec(
        Path.Combine(AppContext.BaseDirectory, "Contracts", "tangerino-employer.json")));

    private readonly JsonElement _root;

    private SwaggerSpec(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        _root = document.RootElement.Clone();
    }

    public static SwaggerSpec Current => Instance.Value;

    public JsonElement Definition(string name) =>
        _root.GetProperty("definitions").TryGetProperty(name, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Definition '{name}' não existe no Swagger.");

    public IReadOnlySet<string> Properties(string definition) =>
        Definition(definition).TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();

    public IReadOnlySet<string> Required(string definition) =>
        Definition(definition).TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();

    public IReadOnlySet<string> Enum(string definition, string property)
    {
        var schema = Definition(definition).GetProperty("properties").GetProperty(property);
        return schema.TryGetProperty("enum", out var values)
            ? values.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal)
            : throw new InvalidOperationException($"{definition}.{property} não é enum.");
    }

    /// <summary>Schema do parâmetro body de uma operação (Swagger 2.0: in=body).</summary>
    public string BodyDefinition(string path, string method)
    {
        var operation = _root.GetProperty("paths").GetProperty(path).GetProperty(method);
        var body = operation.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("in").GetString() == "body");
        return body.GetProperty("schema").GetProperty("$ref").GetString()!.Split('/')[^1];
    }

    public IReadOnlySet<string> QueryParameters(string path, string method) =>
        _root.GetProperty("paths").GetProperty(path).GetProperty(method).TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "query")
                .Select(p => p.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();

    public bool HasOperation(string path, string method) =>
        _root.GetProperty("paths").TryGetProperty(path, out var item) && item.TryGetProperty(method, out _);
}
