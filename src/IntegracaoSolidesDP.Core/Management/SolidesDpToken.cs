namespace IntegracaoSolidesDP.Worker.Api;

public static class SolidesDpToken
{
    private const string BasicPrefix = "Basic ";

    /// <summary>
    /// O token vai como "Authorization: Basic {token}". Quem copia o cabeçalho inteiro do Sólides DP cola
    /// "Basic xxx": o prefixo é retirado para não virar "Basic Basic xxx" (HTTP 401).
    /// </summary>
    public static string? Normalize(string? raw)
    {
        var token = raw?.Trim();
        if (token is not null && token.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[BasicPrefix.Length..].Trim();
        }

        return string.IsNullOrEmpty(token) ? null : token;
    }
}
