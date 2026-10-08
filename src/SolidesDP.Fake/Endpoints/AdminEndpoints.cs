using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Endpoints;

/// <summary>Endpoints de suporte a testes (<c>/_fake/*</c>): sem autenticacao e fora do diario de requests.</summary>
internal static class AdminEndpoints
{
    /// <summary>Opcoes estritas: propriedade desconhecida no corpo administrativo e erro (evita typos silenciosos).</summary>
    private static readonly JsonSerializerOptions Strict = new(FakeJson.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup(FakePaths.AdminPrefix);

        admin.MapGet("/state", ([FromServices] FakeStore store) => Results.Json(store.Snapshot(), FakeJson.Options));

        admin.MapPost("/reset", ([FromServices] FakeStore store, [FromServices] RequestJournal journal, [FromServices] FaultRegistry faults) =>
        {
            store.Reset();
            journal.Clear();
            faults.Clear();
            return Results.NoContent();
        });

        admin.MapGet("/requests", ([FromServices] RequestJournal journal) => Results.Json(journal.Snapshot(), FakeJson.Options));

        admin.MapDelete("/requests", ([FromServices] RequestJournal journal) =>
        {
            journal.Clear();
            return Results.NoContent();
        });

        admin.MapGet("/behavior", ([FromServices] FakeStore store) => Results.Json(store.Behavior, FakeJson.Options));

        admin.MapPut("/behavior", async (HttpContext http, [FromServices] FakeStore store) =>
        {
            var (behavior, error) = await ReadAsync<FakeBehavior>(http);
            if (behavior is null)
            {
                return ApiResults.Spring(http, StatusCodes.Status400BadRequest, error!);
            }

            store.Behavior = behavior;
            return Results.Json(store.Behavior, FakeJson.Options);
        });

        admin.MapGet("/faults", ([FromServices] FaultRegistry faults) => Results.Json(faults.Snapshot(), FakeJson.Options));

        admin.MapPost("/faults", async (HttpContext http, [FromServices] FaultRegistry faults) =>
        {
            var (rule, error) = await ReadAsync<FaultRule>(http);
            var problem = error ?? Validate(rule!);
            if (problem is not null)
            {
                return ApiResults.Spring(http, StatusCodes.Status400BadRequest, problem);
            }

            faults.Add(rule!);
            return Results.Json(rule, FakeJson.Options);
        });

        admin.MapDelete("/faults", ([FromServices] FaultRegistry faults) =>
        {
            faults.Clear();
            return Results.NoContent();
        });
    }

    private static string? Validate(FaultRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Path) || !rule.Path.StartsWith('/'))
        {
            return "'path' is required and must start with '/' (exact path, or prefix when it ends with '*')";
        }

        if (rule.Times is 0 or < -1)
        {
            return "'times' must be >= 1 (or -1 for unlimited)";
        }

        if (rule.Status is { } status && status is < 100 or > 599)
        {
            return "'status' must be a valid HTTP status code";
        }

        return rule.DelayMs is < 0 ? "'delayMs' must be >= 0" : null;
    }

    /// <summary>Le o corpo; devolve o motivo (-> 400) quando vazio, malformado ou com propriedades desconhecidas.</summary>
    private static async Task<(T? Value, string? Error)> ReadAsync<T>(HttpContext http)
        where T : class
    {
        var text = await BodyReader.ReadTextAsync(http);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, $"A {typeof(T).Name} JSON body is required");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Strict) is { } value ? (value, null) : (null, "The JSON body must be an object");
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid {typeof(T).Name} JSON: {ex.Message}");
        }
    }
}
