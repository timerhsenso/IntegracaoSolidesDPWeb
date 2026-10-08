namespace IntegracaoSolidesDP.Worker.Source;

/// <summary>Uma linha de dbo.func1 (com centro de custo e CNPJ da filial). Colunas char vêm sem espaços à direita.</summary>
public sealed record EmployeeRow
{
    public Guid Id { get; init; }
    public string Nomatric { get; init; } = string.Empty;
    public int Cdempresa { get; init; }
    public int Cdfilial { get; init; }
    public string? Nome { get; init; }
    public int TipoColaborador { get; init; }
    public string? Situacao { get; init; }

    /// <summary>tsitu1.fldemissao = 'S' para a situação da linha.</summary>
    public bool SituacaoDeDesligamento { get; init; }

    public DateTime? DataAdmissao { get; init; }
    public DateTime? DataDemissao { get; init; }
    public DateTime? DataTransferencia { get; init; }
    public string? CausaRescisao { get; init; }
    public string? Cpf { get; init; }
    public string? Pis { get; init; }
    public string? Ctps { get; init; }
    public string? SerieCtps { get; init; }
    public DateTime? DataNascimento { get; init; }
    public string? Sexo { get; init; }
    public string? EstadoCivil { get; init; }
    public string? GrauInstrucao { get; init; }
    public int? Raca { get; init; }
    public string? Email { get; init; }
    public string? EmailAlternativo { get; init; }
    public string? Ddd { get; init; }
    public string? Telefone { get; init; }
    public string? NomeMae { get; init; }
    public string? NomePai { get; init; }
    public string? Cargo { get; init; }
    public string? CentroCusto { get; init; }
    public string? CentroCustoDescricao { get; init; }
    public string? Cnpj { get; init; }

    /// <summary>Identidade no DP: "{cdempresa}-{matrícula}". A filial fica de fora porque na ADN ela é o posto/cliente e muda em transferências.</summary>
    public string ExternalId => EmployeeKey.For(Cdempresa, Nomatric);
}

public static class EmployeeKey
{
    /// <summary>Gravado no estado; o worker recusa iniciar se o esquema mudar depois do go-live.</summary>
    public const string Scheme = "empresa-matricula-v1";

    public static string For(int cdempresa, string nomatric) =>
        FormattableString.Invariant($"{cdempresa}-{nomatric.Trim()}");
}

/// <summary>dbo.cargo1.</summary>
public sealed record JobRoleRow
{
    public string Cdcargo { get; init; } = string.Empty;
    public string? Descricao { get; init; }
    public string? Cbo { get; init; }
}

/// <summary>dbo.test1 (estabelecimento = empresa + filial). Na ADN, a filial é o posto/cliente de alocação.</summary>
public sealed record WorkplaceRow
{
    public int Cdempresa { get; init; }
    public int Cdfilial { get; init; }
    public string? NomeFantasia { get; init; }
    public string? Descricao { get; init; }
    public string? Cnpj { get; init; }

    public string ExternalId => WorkplaceKey.For(Cdempresa, Cdfilial);
}

public static class WorkplaceKey
{
    public static string For(int cdempresa, int cdfilial) =>
        FormattableString.Invariant($"{cdempresa}-{cdfilial}");
}

/// <summary>dbo.feria2: um período de gozo (parcela) de férias.</summary>
public sealed record VacationRow
{
    public Guid Id { get; init; }
    public string Nomatric { get; init; } = string.Empty;
    public int Cdempresa { get; init; }
    public int Cdfilial { get; init; }
    public DateTime Inicio { get; init; }
    public DateTime Fim { get; init; }
    public int? Dias { get; init; }
    public int? Abono { get; init; }

    /// <summary>feria2.flconfirm (taux2 tabela 42): 1 Programada, 2 Liberada, 3 Enviada p/ Folha, 4 Calculada, 6 Confirmada, 7 Reprogramada.</summary>
    public int Situacao { get; init; }

    public string EmployeeExternalId => EmployeeKey.For(Cdempresa, Nomatric);
}
