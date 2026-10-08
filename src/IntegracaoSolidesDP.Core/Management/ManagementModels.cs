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
}

/// <summary>Comando retirado da fila (solidesdp.comando) para execução.</summary>
public sealed record ClaimedCommand
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;
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
