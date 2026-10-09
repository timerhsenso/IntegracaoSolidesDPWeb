namespace IntegracaoSolidesDP.Worker.Api;

/// <summary>
/// Conta do Sólides DP em uso no escopo (uma por empresa do RHSenso). Com uma empresa definida,
/// toda chamada do <see cref="SolidesDpClient"/> vai com o token dela; sem empresa vale o
/// SolidesDP:Token do appsettings (comandos de linha de comando sem --empresa).
/// </summary>
public sealed class SolidesDpAccount
{
    public int? Cdempresa { get; private set; }

    public string? Token { get; private set; }

    public void Use(int cdempresa, string token)
    {
        Cdempresa = cdempresa;
        Token = SolidesDpToken.Normalize(token)
                ?? throw new ArgumentException(FormattableString.Invariant($"Token vazio para a empresa {cdempresa}."), nameof(token));
    }

    public void Clear()
    {
        Cdempresa = null;
        Token = null;
    }
}

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
