namespace IntegracaoSolidesDP.Web.Data;

// Linhas lidas das tabelas do serviço (schema solidesdp) e do RHSenso (dbo). Somente leitura.

public sealed record Execucao
{
    public Guid RunId { get; init; }

    /// <summary>Empresa (conta do Sólides DP) da execução; nula nas execuções que pararam antes de chegar a uma empresa.</summary>
    public int? Cdempresa { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public string Origem { get; init; } = string.Empty;
    public string? SolicitadoPor { get; init; }
    public DateTimeOffset Inicio { get; init; }
    public DateTimeOffset? Fim { get; init; }
    public int? ConfigVersao { get; init; }
    public string? ResumoJson { get; init; }
    public string? Erro { get; init; }

    public TimeSpan? Duracao => Fim - Inicio;
}

public sealed record ItemExecucao
{
    public long Id { get; init; }
    public string Entidade { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string Acao { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int? Http { get; init; }
    public string? Mensagem { get; init; }
}

public sealed record RegistroMigrado
{
    public int Cdempresa { get; init; }

    /// <summary>Código do registro, o mesmo dos itens das execuções (colaborador: "{empresa}-{matrícula}").</summary>
    public string ExternalId { get; init; } = string.Empty;

    /// <summary>Colaborador: "criado" pela integração ou "vinculado_cpf" (já existia no Sólides DP).</summary>
    public string? Origem { get; init; }

    /// <summary>Colaborador: Código Externo enviado ao Sólides DP.</summary>
    public string? CodigoExterno { get; init; }
    public string? Nome { get; init; }
    public long? RemoteId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset AtualizadoEm { get; init; }
}

public sealed record FeriasMigradas
{
    public Guid Feria2Id { get; init; }
    public int Cdempresa { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public string? Nome { get; init; }
    public DateTime? Inicio { get; init; }
    public DateTime? Fim { get; init; }
    public long? RemoteId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Tentativas { get; init; }
    public string? UltimoErro { get; init; }
    public DateTimeOffset AtualizadoEm { get; init; }
}

public sealed record HistoricoItem
{
    public DateTimeOffset Data { get; init; }
    public bool DryRun { get; init; }
    public Guid RunId { get; init; }
    public string Entidade { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string Acao { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int? Http { get; init; }
    public string? Mensagem { get; init; }
}

public sealed record Comando
{
    public long Id { get; init; }

    /// <summary>Empresa do pedido; nula = todas as habilitadas.</summary>
    public int? Cdempresa { get; init; }
    public string Tipo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SolicitadoPor { get; init; } = string.Empty;
    public DateTimeOffset SolicitadoEm { get; init; }
    public DateTimeOffset? IniciadoEm { get; init; }
    public DateTimeOffset? ConcluidoEm { get; init; }
    public Guid? RunId { get; init; }
    public string? Resultado { get; init; }
}

public sealed record VersaoConfiguracao
{
    public int Versao { get; init; }
    public bool Ativo { get; init; }
    public string SyncJson { get; init; } = string.Empty;
    public string? Observacao { get; init; }
    public string CriadoPor { get; init; } = string.Empty;
    public DateTimeOffset CriadoEm { get; init; }
}

public sealed record RegistroAuditoria
{
    public long Id { get; init; }
    public DateTimeOffset OcorridoEm { get; init; }
    public string Usuario { get; init; } = string.Empty;
    public string Acao { get; init; } = string.Empty;
    public string? Detalhe { get; init; }
    public string? Ip { get; init; }
}

public sealed record Contagem
{
    public string Tipo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int Quantidade { get; init; }
}

public sealed record ExecucaoNoDia
{
    public DateTimeOffset Inicio { get; init; }
    public string Status { get; init; } = string.Empty;
}
