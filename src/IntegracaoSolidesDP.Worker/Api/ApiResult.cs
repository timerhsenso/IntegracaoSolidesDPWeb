using System.Net;
using System.Text.Json;

namespace IntegracaoSolidesDP.Worker.Api;

public enum ApiOutcome
{
    Success,

    /// <summary>A API recusou (4xx, ou 200 com erro no corpo). Reenviar igual não adianta.</summary>
    Rejected,

    NotFound,

    /// <summary>401/403: token inválido ou sem permissão. Aborta a execução.</summary>
    Unauthorized,

    /// <summary>Falha de transporte, timeout, 429/5xx depois dos retries. Resultado desconhecido.</summary>
    TransportError,
}

public sealed record ApiResult<T>(ApiOutcome Outcome, T? Value, int? HttpStatus, string? Message)
{
    public bool IsSuccess => Outcome == ApiOutcome.Success;

    public static ApiResult<T> Ok(T value, int? status) => new(ApiOutcome.Success, value, status, null);

    public ApiResult<TOther> As<TOther>() => new(Outcome, default, HttpStatus, Message);
}

/// <summary>
/// Classifica respostas do DP. Pontos de atenção do Swagger: <c>/employee/register</c> e
/// <c>/employee/dismiss</c> devolvem <c>ResponseEntity{body, statusCode, statusCodeValue}</c>
/// e os ajustes devolvem <c>{registered, message, entity}</c> — um HTTP 200 pode carregar erro.
/// </summary>
public static class ApiResponseClassifier
{
    public static (ApiOutcome Outcome, string? Message, JsonElement? Payload) Classify(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var json = TryParse(body);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (ApiOutcome.Unauthorized, MessageFrom(json) ?? status.ToString(), json);
        }

        if (status == HttpStatusCode.NotFound)
        {
            return (ApiOutcome.NotFound, MessageFrom(json) ?? "Não encontrado", json);
        }

        if (code == 429 || code >= 500)
        {
            return (ApiOutcome.TransportError, MessageFrom(json) ?? $"HTTP {code}", json);
        }

        if (code >= 400)
        {
            return (ApiOutcome.Rejected, MessageFrom(json) ?? $"HTTP {code}", json);
        }

        if (json is { ValueKind: JsonValueKind.Object } obj)
        {
            // ResponseEntity: o status real vem no corpo.
            if (obj.TryGetProperty("statusCodeValue", out var inner) && inner.TryGetInt32(out var innerCode))
            {
                var payload = obj.TryGetProperty("body", out var b) ? b : (JsonElement?)null;
                return innerCode switch
                {
                    >= 200 and < 300 => (ApiOutcome.Success, null, payload),
                    401 or 403 => (ApiOutcome.Unauthorized, MessageFrom(payload) ?? MessageFrom(obj), payload),
                    404 => (ApiOutcome.NotFound, MessageFrom(payload) ?? MessageFrom(obj), payload),
                    _ => (ApiOutcome.Rejected, MessageFrom(payload) ?? MessageFrom(obj) ?? $"statusCodeValue {innerCode}", payload),
                };
            }

            // AdjustmentLaunch(Only)ResponseDTO.
            if (obj.TryGetProperty("registered", out var registered) && registered.ValueKind == JsonValueKind.False)
            {
                return (ApiOutcome.Rejected, MessageFrom(obj) ?? "registered=false", obj);
            }
        }

        return (ApiOutcome.Success, null, json);
    }

    private static JsonElement? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.Clone();
        }
    }

    private static string? MessageFrom(JsonElement? element)
    {
        if (element is not { } e)
        {
            return null;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Object:
                foreach (var name in (string[])["message", "error", "messages", "body"])
                {
                    if (e.TryGetProperty(name, out var value))
                    {
                        var text = value.ValueKind switch
                        {
                            JsonValueKind.String => value.GetString(),
                            JsonValueKind.Array => string.Join("; ", value.EnumerateArray().Select(v => v.ToString())),
                            JsonValueKind.Object => MessageFrom(value),
                            _ => null,
                        };
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
