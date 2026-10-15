namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>De onde veio o erro.</summary>
public static class OrigensErro
{
    public const string Web = "web";
    public const string Servico = "servico";
}

/// <summary>Situação de um erro na tela "Erros da aplicação".</summary>
public static class SituacoesErro
{
    /// <summary>Novo ou reaberto: conta e (com o envio ligado) avisa.</summary>
    public const string Aberto = "aberto";

    /// <summary>Conhecido: continua sendo registrado e contado, mas não gera e-mail.</summary>
    public const string Silenciado = "silenciado";

    /// <summary>Corrigido: se acontecer de novo, volta a ficar aberto.</summary>
    public const string Resolvido = "resolvido";
}

/// <summary>Como os erros chegam ao suporte por e-mail.</summary>
public static class ModosEnvioErro
{
    public const string Desligado = "desligado";
    public const string Manual = "manual";
    public const string Automatico = "automatico";
}

/// <summary>Um erro técnico capturado (exceção ou log de nível Error), antes de ser gravado.</summary>
public sealed record ErroCapturado
{
    /// <summary><see cref="OrigensErro"/>.</summary>
    public required string Origem { get; init; }

    /// <summary>Classe que registrou (SourceContext do log) ou a ação da Web.</summary>
    public string? Fonte { get; init; }

    /// <summary>Tipo da exceção, quando há.</summary>
    public string? Tipo { get; init; }

    public required string Mensagem { get; init; }

    /// <summary>Detalhe técnico (exceção completa).</summary>
    public string? Detalhe { get; init; }

    /// <summary>Login de quem estava na tela (Web).</summary>
    public string? Usuario { get; init; }

    /// <summary>"GET /Empresas/Editar/15" (Web), sem a query string.</summary>
    public string? Tela { get; init; }

    public int? Cdempresa { get; init; }
    public string? Maquina { get; init; }
    public string? Versao { get; init; }
    public DateTimeOffset Quando { get; init; }

    /// <summary>Execução que caiu (DP ou banco fora): só é registrada com a opção ligada.</summary>
    public bool ExecucaoCaida { get; init; }
}

/// <summary>Um erro registrado (solidesdp.erro_aplicacao): as ocorrências do mesmo erro ficam numa linha só.</summary>
public sealed record ErroAplicacao
{
    public long Id { get; init; }

    /// <summary>O código que o usuário vê e informa ao suporte.</summary>
    public string Codigo => CodigoErro.De(Id);

    public string Origem { get; init; } = string.Empty;
    public string? Fonte { get; init; }
    public string? Tipo { get; init; }
    public string Mensagem { get; init; } = string.Empty;
    public string? Detalhe { get; init; }
    public string? Usuario { get; init; }
    public string? Tela { get; init; }
    public int? Cdempresa { get; init; }
    public string? Maquina { get; init; }
    public string? Versao { get; init; }
    public DateTimeOffset PrimeiraVez { get; init; }
    public DateTimeOffset UltimaVez { get; init; }
    public int Ocorrencias { get; init; }

    /// <summary><see cref="SituacoesErro"/>.</summary>
    public string Situacao { get; init; } = SituacoesErro.Aberto;

    /// <summary>Quem silenciou ou resolveu, e quando.</summary>
    public string? SituacaoPor { get; init; }

    public DateTimeOffset? SituacaoEm { get; init; }
    public bool ExecucaoCaida { get; init; }
}

/// <summary>Configuração dos erros da aplicação (solidesdp.erro_config, uma linha). Sem linha, valem os padrões.</summary>
public sealed record ConfiguracaoErros
{
    /// <summary><see cref="ModosEnvioErro"/>.</summary>
    public string ModoEnvio { get; init; } = ModosEnvioErro.Automatico;

    /// <summary>Erros da Web vão para o suporte.</summary>
    public bool OrigemWeb { get; init; } = true;

    /// <summary>Erros do serviço vão para o suporte.</summary>
    public bool OrigemServico { get; init; } = true;

    /// <summary>Execução que caiu (DP ou banco fora) também é registrada como erro.</summary>
    public bool ExecucaoCaida { get; init; } = true;

    /// <summary>De quantas em quantas horas sai o resumo de um erro que continua acontecendo.</summary>
    public int ResumoHoras { get; init; } = 4;

    /// <summary>Máximo de e-mails de erro por dia.</summary>
    public int LimiteDia { get; init; } = 20;

    /// <summary>O perfil Operador vê a tela "Erros da aplicação".</summary>
    public bool OperadorVe { get; init; }

    /// <summary>O perfil Operador pode enviar um erro ao suporte.</summary>
    public bool OperadorEnvia { get; init; } = true;

    /// <summary>Erros cuja última ocorrência é mais antiga que isto são apagados na limpeza diária. 0 = nunca.</summary>
    public int RetencaoDias { get; init; } = 90;

    /// <summary>Nome do cliente no assunto do e-mail (ex.: ADN).</summary>
    public string? NomeCliente { get; init; }

    public string? AtualizadoPor { get; init; }
    public DateTimeOffset? AtualizadoEm { get; init; }
}

/// <summary>O código do erro: "E-1027".</summary>
public static class CodigoErro
{
    public static string De(long id) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"E-{id}");
}
