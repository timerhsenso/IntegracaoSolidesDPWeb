namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>Conexão com a API do Sólides DP (antiga Tangerino).</summary>
public sealed class SolidesDpOptions
{
    public const string SectionName = "SolidesDP";

    /// <summary>
    /// Hosts da API real. O Sólides DP não tem ambiente de homologação: qualquer
    /// chamada para estes hosts escreve na conta de produção do cliente.
    /// </summary>
    public static readonly IReadOnlyList<string> ProductionHosts = ["employer.tangerino.com.br"];

    public string BaseUrl { get; set; } = "https://employer.tangerino.com.br";

    /// <summary>Token gerado no Sólides DP em Empregador → Integrações. Enviado como <c>Authorization: Basic {token}</c>.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Timeout por tentativa; o timeout total da requisição é 4x este valor.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Envia <c>skipUnifiedSync=true</c> no cadastro de colaborador para não propagar
    /// o registro à base unificada (CUC) da Sólides. Manter true até a Sólides confirmar
    /// se o DP e o Gestão da ADN compartilham a CUC.
    /// </summary>
    public bool SkipUnifiedSync { get; set; } = true;

    /// <summary>Permite apontar para a API real fora de Production (execução supervisionada).</summary>
    public bool AllowProductionApiOutsideProduction { get; set; }

    public static bool IsProductionHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && ProductionHosts.Any(h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase));
}
