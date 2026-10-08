using System.Text.Json;

namespace SolidesDP.Fake.Tests.Contracts;

/// <summary>
/// Helper minimo de Swagger 2.0: le <c>spec/tangerino-employer.json</c> (copiado para a saida de teste) e valida respostas
/// JSON recursivamente contra as <c>definitions</c> (resolvendo <c>$ref</c> e nomes genericos com «»).
/// Implementacao independente da usada pelo fake para validar requests (de proposito: so assim o teste nao e circular).
/// </summary>
public sealed class SwaggerContract
{
    /// <summary>
    /// Nomes das constantes do <c>org.springframework.http.HttpStatus</c>. O Swagger lista <c>ResponseEntity.statusCode</c> como
    /// enum de numeros em string ("200"), mas o Spring serializa o NOME da constante ("OK"); as duas formas sao aceitas.
    /// </summary>
    private static readonly HashSet<string> SpringStatusNames =
    [
        "OK", "CREATED", "ACCEPTED", "NO_CONTENT", "BAD_REQUEST", "UNAUTHORIZED", "FORBIDDEN", "NOT_FOUND", "METHOD_NOT_ALLOWED",
        "CONFLICT", "UNSUPPORTED_MEDIA_TYPE", "UNPROCESSABLE_ENTITY", "TOO_MANY_REQUESTS", "INTERNAL_SERVER_ERROR",
        "BAD_GATEWAY", "SERVICE_UNAVAILABLE", "GATEWAY_TIMEOUT",
    ];

    private readonly JsonObject _root;

    private SwaggerContract(JsonObject root) => _root = root;

    private JsonObject Paths => (JsonObject)_root["paths"]!;

    private JsonObject Definitions => (JsonObject)_root["definitions"]!;

    /// <summary>Carrega o Swagger copiado para <c>Contracts/tangerino-employer.json</c> na saida do teste.</summary>
    public static SwaggerContract Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Contracts", "tangerino-employer.json");
        return new SwaggerContract((JsonObject)JsonNode.Parse(File.ReadAllText(file))!);
    }

    /// <summary>Nome de uma definition generica, ex.: <c>Generic("Page", "JobRoleDTO")</c> = <c>Page«JobRoleDTO»</c>.</summary>
    public static string Generic(string outer, string inner) => $"{outer}«{inner}»";

    /// <summary>Existe operacao <paramref name="method"/> em <paramref name="path"/> no Swagger?</summary>
    public bool HasOperation(string method, string path) => Paths[path] is JsonObject item && item[method.ToLowerInvariant()] is not null;

    /// <summary>Schema da resposta (status 200 por padrao) de uma operacao; nulo se a operacao nao declara schema.</summary>
    public JsonObject? ResponseSchema(string method, string path, int status = 200) =>
        Paths[path]?[method.ToLowerInvariant()]?["responses"]?[status.ToString()]?["schema"] as JsonObject;

    /// <summary>Nome da definition referenciada pelo schema da resposta (nulo para schema inline como <c>string</c>).</summary>
    public string? ResponseDefinitionName(string method, string path, int status = 200) =>
        ResponseSchema(method, path, status) is { } schema ? ReferenceName(schema) : null;

    /// <summary>Valida <paramref name="value"/> contra a definition e devolve as violacoes (lista vazia = conforme).</summary>
    public IReadOnlyList<string> Validate(JsonNode? value, string definitionName)
    {
        var violations = new List<string>();
        ValidateNode(value, new JsonObject { ["$ref"] = "#/definitions/" + definitionName }, "$", violations);
        return violations;
    }

    /// <summary>Falha o teste listando todas as violacoes encontradas.</summary>
    public void AssertConforms(JsonNode? value, string definitionName)
    {
        var violations = Validate(value, definitionName);
        Assert.True(violations.Count == 0, $"Resposta nao conforme ao Swagger ({definitionName}):{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", violations)}{Environment.NewLine}JSON: {value?.ToJsonString()}");
    }

    private static string? ReferenceName(JsonObject schema)
    {
        var reference = (schema["originalRef"] ?? schema["$ref"])?.GetValue<string>();
        if (reference is null)
        {
            return null;
        }

        const string prefix = "#/definitions/";
        return Uri.UnescapeDataString(reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : reference);
    }

    private void ValidateNode(JsonNode? node, JsonObject rawSchema, string path, List<string> violations, string? parentDefinition = null, string? propertyName = null)
    {
        if (node is null)
        {
            return; // null e valido em Java (campo nao preenchido)
        }

        var name = ReferenceName(rawSchema);
        var schema = name is null ? rawSchema : (JsonObject)(Definitions[name] ?? throw new InvalidOperationException($"Definition '{name}' inexistente no Swagger"));
        var type = schema["type"]?.GetValue<string>();

        if (type == "array")
        {
            ValidateArray(node, schema, path, violations);
        }
        else if (type == "object" || schema["properties"] is not null)
        {
            ValidateObject(node, schema, name, path, violations);
        }
        else
        {
            ValidateScalar(node, schema, type, path, violations, parentDefinition == "ResponseEntity" && propertyName == "statusCode");
        }
    }

    private void ValidateArray(JsonNode node, JsonObject schema, string path, List<string> violations)
    {
        if (node is not JsonArray array)
        {
            violations.Add($"{path}: esperado array, veio {node.GetValueKind()}");
            return;
        }

        for (var i = 0; i < array.Count; i++)
        {
            ValidateNode(array[i], (JsonObject)schema["items"]!, $"{path}[{i}]", violations);
        }
    }

    private void ValidateObject(JsonNode node, JsonObject schema, string? definitionName, string path, List<string> violations)
    {
        if (node is not JsonObject obj)
        {
            violations.Add($"{path}: esperado objeto, veio {node.GetValueKind()}");
            return;
        }

        if (schema["properties"] is not JsonObject properties)
        {
            return; // objeto livre (ex.: Sort e ResponseEntity.body: o Swagger nao descreve as propriedades)
        }

        foreach (var (property, value) in obj)
        {
            if (properties[property] is JsonObject propertySchema)
            {
                ValidateNode(value, propertySchema, $"{path}.{property}", violations, definitionName, property);
            }
            else
            {
                violations.Add($"{path}.{property}: propriedade inexistente na definition '{definitionName}'");
            }
        }
    }

    private static void ValidateScalar(JsonNode node, JsonObject schema, string? type, string path, List<string> violations, bool springStatusName)
    {
        var kind = node.GetValueKind();
        var format = schema["format"]?.GetValue<string>();

        switch (type)
        {
            case "string":
                if (format == "date-time" && IsInteger(node, kind, "int64"))
                {
                    return; // java.util.Date: epoch ms (docs do fornecedor) ou ISO-8601
                }

                if (kind != JsonValueKind.String)
                {
                    violations.Add($"{path}: esperado string, veio {kind}");
                    return;
                }

                var text = node.GetValue<string>();
                if (schema["enum"] is JsonArray allowed && !allowed.Any(a => a!.GetValue<string>() == text) && !(springStatusName && SpringStatusNames.Contains(text)))
                {
                    violations.Add($"{path}: valor '{text}' fora do enum do Swagger");
                }
                else if (format == "date-time" && !DateTimeOffset.TryParse(text, out _))
                {
                    violations.Add($"{path}: '{text}' nao e um date-time");
                }

                break;
            case "integer":
                if (!IsInteger(node, kind, format))
                {
                    violations.Add($"{path}: esperado inteiro ({format}), veio {node.ToJsonString()}");
                }

                break;
            case "number":
                if (kind != JsonValueKind.Number)
                {
                    violations.Add($"{path}: esperado numero, veio {kind}");
                }

                break;
            case "boolean":
                if (kind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    violations.Add($"{path}: esperado boolean, veio {kind}");
                }

                break;
            default:
                break;
        }
    }

    private static bool IsInteger(JsonNode node, JsonValueKind kind, string? format) =>
        kind == JsonValueKind.Number
        && node is JsonValue value
        && value.TryGetValue<long>(out var number)
        && (format != "int32" || number is >= int.MinValue and <= int.MaxValue);
}
