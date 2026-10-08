using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SolidesDP.Fake;

/// <summary>Opcoes de JSON compartilhadas pelo fake e pelo <c>Testing.FakeAdminClient</c>: camelCase, sem nulos, enums como string.</summary>
public static class FakeJson
{
    /// <summary>Opcoes padrao (camelCase, leitura case-insensitive, nulos omitidos, enums por nome).</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // UTF-8 legivel (FÉRIAS, +00:00), como o Jackson
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Helpers para montar/ler <see cref="JsonObject"/> (os nulos nunca sao escritos).</summary>
internal static class JsonObjectExtensions
{
    public static void Put(this JsonObject target, string name, string? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    public static void Put(this JsonObject target, string name, long? value)
    {
        if (value is { } v)
        {
            target[name] = v;
        }
    }

    public static void Put(this JsonObject target, string name, bool? value)
    {
        if (value is { } v)
        {
            target[name] = v;
        }
    }

    public static void PutNode(this JsonObject target, string name, JsonNode? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    public static string? GetString(this JsonObject source, string name) =>
        source[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? GetLong(this JsonObject source, string name) =>
        source[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    public static bool? GetBool(this JsonObject source, string name) =>
        source[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static JsonObject? GetObject(this JsonObject source, string name) => source[name] as JsonObject;

    /// <summary>Serializa um registro com as opcoes do fake.</summary>
    public static JsonNode ToNode<T>(this T value) => JsonSerializer.SerializeToNode(value, FakeJson.Options)!;
}
