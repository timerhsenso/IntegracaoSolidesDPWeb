namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Uma versão de solidesdp.configuracao. A vigente é a de maior versão da instância.</summary>
public sealed record ConfigurationVersion
{
    public int Version { get; init; }
    public bool Active { get; init; }
    public string SyncJson { get; init; } = string.Empty;
    public string? Note { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>solidesdp.configuracao.id (chave das tabelas filhas da versão).</summary>
    public int Id { get; init; }

    /// <summary>Empresas configuradas nesta versão (solidesdp.configuracao_empresa e configuracao_filial).</summary>
    public IReadOnlyList<EmpresaConfiguracao> Empresas { get; init; } = [];
}

/// <summary>
/// Uma empresa do RHSenso (dbo.temp1) numa versão da configuração. Cada empresa é uma conta do Sólides DP, com o
/// seu token (solidesdp.empresa_token). Tudo o que depende da conta ou da fase da implantação é da empresa; as regras
/// da folha (tipos, situações, férias, travas) são gerais (seção Sync), ver <see cref="EmpresaOptions"/>.
/// </summary>
public sealed record EmpresaConfiguracao
{
    public int Cdempresa { get; init; }
    public bool Habilitada { get; init; }

    /// <summary>Simulação desta empresa (nada é gravado no Sólides DP). A chave geral é ativar/desativar a integração.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Início do uso do Sólides DP nesta empresa. Obrigatória para o envio real.</summary>
    public DateOnly? GoLiveDate { get; init; }

    /// <summary>externalId da escala dos novos colaboradores nesta conta; vazio = a padrão da conta.</summary>
    public string? WorkScheduleExternalId { get; init; }

    /// <summary>externalId da regra de ponto dos novos colaboradores nesta conta; vazio = a padrão da conta.</summary>
    public string? PunchRuleExternalId { get; init; }

    /// <summary>Id do motivo de ajuste FÉRIAS nesta conta; vazio = procurar pela descrição.</summary>
    public long? FeriasMotivoId { get; init; }

    /// <summary>"PorCnpj" (padrão) ou "Nenhuma" (conta com uma só empresa no DP).</summary>
    public string? ModoEmpresa { get; init; }

    /// <summary>Cria no Sólides DP a empresa (CNPJ) que não existir na conta.</summary>
    public bool CriarEmpresasFaltantes { get; init; }

    /// <summary>Filiais (dbo.test1.cdfilial) que entram. Vazio = todas as filiais da empresa.</summary>
    public IReadOnlyList<int> Filiais { get; init; } = [];

    /// <summary>Piloto: só estes colaboradores (CPF, matrícula ou "{empresa}-{matrícula}"). Vazio = todos.</summary>
    public IReadOnlyList<string> Piloto { get; init; } = [];
}

/// <summary>Token de uma empresa (linha mais recente de solidesdp.empresa_token), ainda cifrado.</summary>
public sealed record EmpresaToken
{
    public int Cdempresa { get; init; }

    /// <summary>Token cifrado (<see cref="ITokenProtector"/>); nulo = token removido.</summary>
    public byte[]? TokenCifrado { get; init; }

    public string CriadoPor { get; init; } = string.Empty;
    public DateTimeOffset CriadoEm { get; init; }
}

/// <summary>Comando retirado da fila (solidesdp.comando) para execução.</summary>
public sealed record ClaimedCommand
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>Empresa do pedido; nulo = todas as empresas habilitadas.</summary>
    public int? Cdempresa { get; init; }
}

/// <summary>Tipos aceitos em solidesdp.comando.tipo.</summary>
public static class CommandTypes
{
    public const string Run = "EXECUTAR";
    public const string DryRun = "EXECUTAR_DRYRUN";
    public const string CheckConfig = "CHECK_CONFIG";
    public const string Discover = "DISCOVER";
    public const string Reconcile = "RECONCILE";

    public static readonly IReadOnlyList<string> All = [Run, DryRun, CheckConfig, Discover, Reconcile];
}

/// <summary>Valores de solidesdp.comando.status.</summary>
public static class CommandStatuses
{
    public const string Pending = "pendente";
    public const string Running = "executando";
    public const string Done = "concluido";
    public const string Failed = "falhou";
}

/// <summary>Usuário gravado quando a ação é do próprio serviço (ex.: versão 1 da configuração).</summary>
public static class ManagementUsers
{
    public const string System = "sistema";
}

/// <summary>Valores de <see cref="EmpresaConfiguracao.ModoEmpresa"/> (solidesdp.configuracao_empresa.modo_empresa).</summary>
public static class ModosEmpresa
{
    public const string Nenhuma = "Nenhuma";
    public const string PorCnpj = "PorCnpj";
}
