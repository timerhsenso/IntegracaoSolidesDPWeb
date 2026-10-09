namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>
/// Gestão pela aplicação Web (IntegracaoSolidesDP.Web). Desligada, o serviço funciona como antes:
/// a configuração vem só do appsettings.json e não há fila de comandos.
/// </summary>
public sealed class ManagementOptions
{
    public const string SectionName = "Gestao";

    /// <summary>
    /// Ligada: as regras da seção Sync passam a vir da versão vigente de <c>solidesdp.configuracao</c>
    /// (a primeira versão é criada a partir do appsettings.json), a integração pode ser
    /// ativada/desativada e o serviço atende os comandos de <c>solidesdp.comando</c>.
    /// </summary>
    public bool Habilitada { get; set; }

    /// <summary>De quanto em quanto tempo o serviço procura comandos pendentes (ex.: "Executar agora").</summary>
    public TimeSpan IntervaloComandos { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Chave (32 bytes em base64) que cifra os tokens do Sólides DP das empresas (AES-256-GCM). Tem de ser a
    /// mesma do Web:ChaveTokens. Vazia, no Windows, os tokens são protegidos pela máquina (DPAPI): Web e serviço
    /// precisam estar no mesmo servidor. Guardar em variável de ambiente ou user-secrets, nunca no repositório.
    /// </summary>
    public string? ChaveTokens { get; set; }
}
