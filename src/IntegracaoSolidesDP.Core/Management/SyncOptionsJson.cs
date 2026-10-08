using System.Text.Json;
using System.Text.Json.Serialization;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Formato da seção Sync em solidesdp.configuracao.sync_json: os mesmos nomes de chave do
/// appsettings.json (PascalCase) e enums por nome, para o JSON ser legível e editável na Web.
/// </summary>
public static class SyncOptionsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SyncOptions options) => JsonSerializer.Serialize(options, Options);

    /// <exception cref="JsonException">JSON inválido ou incompatível com <see cref="SyncOptions"/>.</exception>
    public static SyncOptions Deserialize(string json) =>
        JsonSerializer.Deserialize<SyncOptions>(json, Options)
        ?? throw new JsonException("sync_json vazio.");
}
