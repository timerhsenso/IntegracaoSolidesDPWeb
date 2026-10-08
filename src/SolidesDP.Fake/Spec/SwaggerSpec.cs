using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolidesDP.Fake.Spec;

/// <summary>Swagger 2.0 do fornecedor (<c>spec/tangerino-employer.json</c>), embutido no assembly.</summary>
internal static class SwaggerSpec
{
    private const string ResourceName = "tangerino-employer.json";
    private const string DefinitionPrefix = "#/definitions/";

    private static readonly Lazy<JsonObject> DefinitionsLazy = new(LoadDefinitions);

    public static JsonObject Definitions => DefinitionsLazy.Value;

    public static JsonObject Definition(string name) =>
        Definitions[name] as JsonObject ?? throw new InvalidOperationException($"Definition '{name}' nao existe no Swagger.");

    /// <summary>Resolve um schema que seja <c>$ref</c> para a definition apontada; schemas inline voltam como estao.</summary>
    public static JsonObject Resolve(JsonObject schema)
    {
        var reference = (schema["originalRef"] ?? schema["$ref"])?.GetValue<string>();
        if (reference is null)
        {
            return schema;
        }

        var name = reference.StartsWith(DefinitionPrefix, StringComparison.Ordinal) ? reference[DefinitionPrefix.Length..] : reference;
        return Definition(Uri.UnescapeDataString(name));
    }

    private static JsonObject LoadDefinitions()
    {
        using var stream = typeof(SwaggerSpec).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso embutido '{ResourceName}' nao encontrado.");
        var root = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("Swagger invalido.");
        return root["definitions"] as JsonObject ?? throw new JsonException("Swagger sem 'definitions'.");
    }
}

/// <summary>Categoria de um problema encontrado ao validar um request contra o Swagger.</summary>
internal enum SchemaIssueKind
{
    UnknownField,
    InvalidValue,
    MissingRequired,
}

/// <summary>Problema encontrado ao validar um request contra o Swagger.</summary>
internal sealed record SchemaIssue(SchemaIssueKind Kind, string Path, string Message);

/// <summary>
/// Valida o corpo de um request contra uma <c>definition</c> do Swagger: propriedades desconhecidas, tipos, enums
/// e (na raiz) a lista <c>required</c>. Nulos sao sempre aceitos nas propriedades; <c>date-time</c> aceita string ISO
/// ou epoch ms (o Jackson aceita as duas formas para <c>java.util.Date</c>).
/// </summary>
internal static class SchemaValidator
{
    public static IReadOnlyList<SchemaIssue> Validate(JsonNode? body, string definitionName, bool rejectUnknownFields)
    {
        var issues = new List<SchemaIssue>();
        var schema = SwaggerSpec.Definition(definitionName);
        ValidateNode(body, schema, "$", rejectUnknownFields, issues);

        if (body is JsonObject root && schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r!.GetValue<string>()))
            {
                if (IsBlank(root[name]))
                {
                    issues.Add(new SchemaIssue(SchemaIssueKind.MissingRequired, name, $"'{name}' is required"));
                }
            }
        }

        return issues;
    }

    private static bool IsBlank(JsonNode? node) =>
        node is null || (node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));

    private static void ValidateNode(JsonNode? node, JsonObject rawSchema, string path, bool rejectUnknown, List<SchemaIssue> issues)
    {
        if (node is null)
        {
            return;
        }

        var schema = SwaggerSpec.Resolve(rawSchema);
        var type = schema["type"]?.GetValue<string>();

        if (type == "array")
        {
            if (node is not JsonArray array)
            {
                issues.Add(Invalid(path, "expected an array"));
                return;
            }

            if (schema["items"] is JsonObject items)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    ValidateNode(array[i], items, $"{path}[{i}]", rejectUnknown, issues);
                }
            }

            return;
        }

        if (type == "object" || schema["properties"] is not null)
        {
            ValidateObject(node, schema, path, rejectUnknown, issues);
            return;
        }

        ValidateScalar(node, schema, type, path, issues);
    }

    private static void ValidateObject(JsonNode node, JsonObject schema, string path, bool rejectUnknown, List<SchemaIssue> issues)
    {
        if (node is not JsonObject obj)
        {
            issues.Add(Invalid(path, "expected an object"));
            return;
        }

        if (schema["properties"] is not JsonObject properties)
        {
            return; // objeto livre (ex.: Sort, ResponseEntity.body)
        }

        foreach (var (name, value) in obj)
        {
            var propertyPath = path == "$" ? name : $"{path}.{name}";
            if (properties[name] is JsonObject propertySchema)
            {
                ValidateNode(value, propertySchema, propertyPath, rejectUnknown, issues);
            }
            else if (rejectUnknown)
            {
                issues.Add(new SchemaIssue(SchemaIssueKind.UnknownField, propertyPath, $"Unrecognized field '{propertyPath}'"));
            }
        }
    }

    private static void ValidateScalar(JsonNode node, JsonObject schema, string? type, string path, List<SchemaIssue> issues)
    {
        var kind = node.GetValueKind();
        switch (type)
        {
            case "string":
                ValidateString(node, schema, kind, path, issues);
                break;
            case "integer":
                if (!IsInteger(node, kind, schema["format"]?.GetValue<string>()))
                {
                    issues.Add(Invalid(path, "expected an integer"));
                }

                break;
            case "number":
                if (kind != JsonValueKind.Number)
                {
                    issues.Add(Invalid(path, "expected a number"));
                }

                break;
            case "boolean":
                if (kind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    issues.Add(Invalid(path, "expected a boolean"));
                }

                break;
            default:
                break;
        }
    }

    private static void ValidateString(JsonNode node, JsonObject schema, JsonValueKind kind, string path, List<SchemaIssue> issues)
    {
        var isDateTime = schema["format"]?.GetValue<string>() == "date-time";
        if (isDateTime && IsInteger(node, kind, "int64"))
        {
            return;
        }

        if (kind != JsonValueKind.String)
        {
            issues.Add(Invalid(path, "expected a string"));
            return;
        }

        var text = node.GetValue<string>();
        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => a!.GetValue<string>() == text))
        {
            var options = string.Join(", ", allowed.Select(a => a!.GetValue<string>()).Take(12));
            var suffix = allowed.Count > 12 ? ", ..." : string.Empty;
            issues.Add(Invalid(path, $"invalid value '{text}'; expected one of [{options}{suffix}]"));
        }
        else if (isDateTime && !DateTimeOffset.TryParse(text, out _))
        {
            issues.Add(Invalid(path, $"invalid date-time '{text}'"));
        }
    }

    private static bool IsInteger(JsonNode node, JsonValueKind kind, string? format)
    {
        if (kind != JsonValueKind.Number || node is not JsonValue value || !value.TryGetValue<long>(out var number))
        {
            return false;
        }

        return format != "int32" || number is >= int.MinValue and <= int.MaxValue;
    }

    private static SchemaIssue Invalid(string path, string message) => new(SchemaIssueKind.InvalidValue, path, $"Invalid '{path}': {message}");
}
