namespace SolidesDP.Fake.Configuration;

/// <summary>Tipo de falha injetada.</summary>
public enum FaultKind
{
    /// <summary>Responde com o <see cref="FaultRule.Status"/> HTTP indicado (ex.: 429/500/503), sem executar a operacao.</summary>
    Status,

    /// <summary>Espera <see cref="FaultRule.DelayMs"/> ms e depois processa normalmente.</summary>
    Delay,

    /// <summary>Nao executa a operacao e devolve o formato de erro do endpoint para o <see cref="ErrorStyle"/> corrente.</summary>
    ErrorInBody,

    /// <summary>Executa e persiste a operacao e depois derruba a conexao, de modo que o cliente nunca recebe a resposta.</summary>
    CommitThenDrop,
}

/// <summary>Regra de falha: consumida a cada request que casa com <see cref="Method"/> e <see cref="Path"/>.</summary>
public sealed record FaultRule
{
    /// <summary>Metodo HTTP (nulo casa com qualquer metodo).</summary>
    public string? Method { get; set; }

    /// <summary>Caminho exato (ex.: <c>/adjustment/register</c>) ou prefixo quando termina em <c>*</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>Quantos requests a regra consome; <c>-1</c> = ilimitado. Padrao 1.</summary>
    public int Times { get; set; } = 1;

    /// <summary>Tipo de falha.</summary>
    public FaultKind Kind { get; set; } = FaultKind.Status;

    /// <summary>Status HTTP (para <see cref="FaultKind.Status"/>, padrao 500; para <see cref="FaultKind.ErrorInBody"/>, padrao 400).</summary>
    public int? Status { get; set; }

    /// <summary>Quando informado, adiciona o header <c>Retry-After</c> (Status e ErrorInBody).</summary>
    public int? RetryAfterSeconds { get; set; }

    /// <summary>Atraso em ms para <see cref="FaultKind.Delay"/> (padrao 1000).</summary>
    public int? DelayMs { get; set; }

    /// <summary>Mensagem do erro injetado.</summary>
    public string? Message { get; set; }
}

/// <summary>Regra de falha ativa com o numero de consumos restantes (devolvida por <c>GET /_fake/faults</c>).</summary>
public sealed record ActiveFaultRule
{
    /// <summary>Regra configurada.</summary>
    public FaultRule Rule { get; set; } = new();

    /// <summary>Consumos restantes (<c>-1</c> = ilimitado).</summary>
    public int Remaining { get; set; }
}
