using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;

namespace IntegracaoSolidesDP.Worker.Mapping;

public static class PayloadHasher
{
    /// <summary>
    /// Incrementar quando o mapeamento mudar: muda todos os hashes e força um reenvio único.
    /// </summary>
    public const int MapperVersion = 1;

    public static string Hash<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, SolidesDpJson.Options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"v{MapperVersion}:{json}"));
        return Convert.ToHexStringLower(bytes);
    }
}
