using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Spec;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Http;

/// <summary>Caminhos reservados do fake.</summary>
internal static class FakePaths
{
    public const string AdminPrefix = "/_fake";

    public static bool IsAdmin(PathString path) => path.StartsWithSegments(AdminPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Leitura (com cache) do corpo do request; o diario e os handlers compartilham a mesma leitura.</summary>
internal static class BodyReader
{
    private const string ItemKey = "fake.body";

    public static async Task<string> ReadTextAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var cached))
        {
            return (string)cached!;
        }

        var text = string.Empty;
        if (http.Request.ContentLength != 0)
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = await reader.ReadToEndAsync(http.RequestAborted);
        }

        http.Items[ItemKey] = text;
        return text;
    }
}

/// <summary>Contexto de um request da API do fornecedor: acesso a store, comportamento corrente, query e corpo validado.</summary>
internal sealed class ApiRequest(HttpContext http)
{
    public HttpContext Http { get; } = http;

    public FakeStore Store { get; } = http.RequestServices.GetRequiredService<FakeStore>();

    public TimeProvider Time { get; } = http.RequestServices.GetRequiredService<TimeProvider>();

    /// <summary>Perfil de comportamento capturado no inicio do request.</summary>
    public FakeBehavior Behavior { get; } = http.RequestServices.GetRequiredService<FakeStore>().Behavior;

    public IQueryCollection Query => Http.Request.Query;

    /// <summary>Instante corrente em epoch ms.</summary>
    public long NowMillis => Time.GetUtcNow().ToUnixTimeMilliseconds();

    public PageRequest Page => PageRequest.From(Query);

    public string? QueryString(string name)
    {
        var value = Query[name].LastOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public long? QueryLong(string name)
    {
        var raw = QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ApiException(ApiError.TransportFailure(
                ErrorCodes.InvalidValue,
                $"Failed to convert value of type 'java.lang.String' to required type 'java.lang.Long'; For input string: \"{raw}\""));
    }

    public bool? QueryBool(string name)
    {
        var raw = QueryString(name);
        if (raw is null)
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" => true,
            "false" or "0" or "off" or "no" => false,
            _ => throw new ApiException(ApiError.TransportFailure(
                ErrorCodes.InvalidValue,
                $"Failed to convert value of type 'java.lang.String' to required type 'boolean'; Invalid boolean value '{raw}'")),
        };
    }

    /// <summary>
    /// Le o corpo como JSON objeto e o valida contra a definition do Swagger. Falhas de transporte (corpo ausente, JSON
    /// malformado, Content-Type) sao sempre HTTP 4xx Spring; as demais seguem o <see cref="ErrorStyle"/>.
    /// </summary>
    public async Task<JsonObject> ReadJsonAsync(string definitionName)
    {
        EnsureJsonContentType();

        var text = await BodyReader.ReadTextAsync(Http);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ApiException(ApiError.TransportFailure(ErrorCodes.InvalidJson, "Required request body is missing"));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new ApiException(ApiError.TransportFailure(ErrorCodes.InvalidJson, $"JSON parse error: {ex.Message}"));
        }

        if (node is not JsonObject body)
        {
            throw new ApiException(ApiError.TransportFailure(
                ErrorCodes.InvalidJson,
                "JSON parse error: Cannot deserialize value of the expected object type from a non-object JSON value"));
        }

        ThrowIfInvalid(SchemaValidator.Validate(body, definitionName, Behavior.RejectUnknownFields));
        return body;
    }

    private static void ThrowIfInvalid(IReadOnlyList<SchemaIssue> issues)
    {
        foreach (var (kind, code) in new[]
        {
            (SchemaIssueKind.UnknownField, ErrorCodes.UnknownField),
            (SchemaIssueKind.InvalidValue, ErrorCodes.InvalidValue),
            (SchemaIssueKind.MissingRequired, ErrorCodes.RequiredField),
        })
        {
            var matching = issues.Where(i => i.Kind == kind).Select(i => i.Message).ToList();
            if (matching.Count > 0)
            {
                throw new ApiException(ApiError.BadRequest(code, string.Join("; ", matching.Take(10))));
            }
        }
    }

    private void EnsureJsonContentType()
    {
        var contentType = Http.Request.ContentType;
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType.Value is { } mediaType
            && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new ApiException(ApiError.TransportFailure(
            ErrorCodes.UnsupportedMediaType,
            $"Content type '{contentType}' not supported",
            StatusCodes.Status415UnsupportedMediaType));
    }
}
