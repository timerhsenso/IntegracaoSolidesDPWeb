using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using SolidesDP.Fake.Configuration;

namespace SolidesDP.Fake.Http;

/// <summary>Montagem das respostas JSON: sucesso, <c>ResponseEntity</c>, corpo de erro Spring e <c>BaseItemDTO</c>.</summary>
internal static class ApiResults
{
    public const string JsonContentType = "application/json; charset=utf-8";
    public const string ErrorCodeHeader = "X-Fake-Error-Code";

    private static readonly Dictionary<int, string> SpringStatusNames = new()
    {
        [200] = "OK",
        [201] = "CREATED",
        [202] = "ACCEPTED",
        [204] = "NO_CONTENT",
        [400] = "BAD_REQUEST",
        [401] = "UNAUTHORIZED",
        [403] = "FORBIDDEN",
        [404] = "NOT_FOUND",
        [405] = "METHOD_NOT_ALLOWED",
        [409] = "CONFLICT",
        [415] = "UNSUPPORTED_MEDIA_TYPE",
        [422] = "UNPROCESSABLE_ENTITY",
        [429] = "TOO_MANY_REQUESTS",
        [500] = "INTERNAL_SERVER_ERROR",
        [502] = "BAD_GATEWAY",
        [503] = "SERVICE_UNAVAILABLE",
        [504] = "GATEWAY_TIMEOUT",
    };

    public static IResult Json(JsonNode node, int status = StatusCodes.Status200OK) =>
        Results.Json(node, FakeJson.Options, JsonContentType, status);

    /// <summary>Nome da constante do <c>org.springframework.http.HttpStatus</c> (ex.: <c>CREATED</c>).</summary>
    public static string SpringStatusName(int status) =>
        SpringStatusNames.TryGetValue(status, out var name) ? name : status.ToString(CultureInfo.InvariantCulture);

    /// <summary>Sucesso de endpoint <c>ResponseEntity</c>: <c>{"body":..., "statusCode":"OK|CREATED", "statusCodeValue":200|201}</c>.</summary>
    public static JsonObject ResponseEntity(JsonNode body, int status) => new()
    {
        ["body"] = body,
        ["statusCode"] = SpringStatusName(status),
        ["statusCodeValue"] = status,
    };

    /// <summary>Envelope <c>BaseItemDTO</c>: <c>{"code":200,"status":"OK","messages":[],"item":...}</c>.</summary>
    public static JsonObject BaseItem(JsonNode item) => new()
    {
        ["code"] = 200,
        ["status"] = "OK",
        ["messages"] = new JsonArray(),
        ["item"] = item,
    };

    /// <summary>Corpo de erro no estilo Spring Boot: <c>{timestamp, status, error, message, path}</c>.</summary>
    public static JsonObject SpringBody(HttpContext http, int status, string message)
    {
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        return new JsonObject
        {
            ["timestamp"] = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["status"] = status,
            ["error"] = ReasonPhrases.GetReasonPhrase(status),
            ["message"] = message,
            ["path"] = http.Request.Path.Value,
        };
    }

    public static IResult Spring(HttpContext http, int status, string message) => Json(SpringBody(http, status, message), status);

    /// <summary>Escreve diretamente um erro Spring (para middlewares).</summary>
    public static Task WriteSpringAsync(HttpContext http, int status, string message) =>
        Spring(http, status, message).ExecuteAsync(http);

    /// <summary>Renderiza um erro de validacao/negocio no formato do endpoint para o <see cref="ErrorStyle"/> corrente.</summary>
    public static IResult Error(HttpContext http, ApiError error, ErrorShape shape, ErrorStyle style)
    {
        http.Response.Headers[ErrorCodeHeader] = error.Code;

        if (error.Transport || style == ErrorStyle.Http || shape == ErrorShape.Spring)
        {
            return Spring(http, error.Status, error.Message);
        }

        return shape == ErrorShape.ResponseEntity
            ? Json(ResponseEntity(new JsonObject { ["message"] = error.Message, ["error"] = error.Code }, error.Status))
            : Json(new JsonObject { ["registered"] = false, ["message"] = error.Message });
    }
}
