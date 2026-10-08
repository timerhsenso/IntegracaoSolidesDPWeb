namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Configuração da aplicação Web (seção "Web" do appsettings.json).</summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Instância do serviço gerenciada por esta Web. Igual a Sync:InstanceName do serviço.</summary>
    public string InstanceName { get; set; } = "default";

    /// <summary>Fuso (IANA) usado para exibir datas e agrupar execuções por dia.</summary>
    public string TimeZone { get; set; } = "America/Bahia";

    /// <summary>Aplica as migrations do login (schema solidesdp_auth) ao iniciar.</summary>
    public bool AplicarMigrationsNoStart { get; set; } = true;
}
