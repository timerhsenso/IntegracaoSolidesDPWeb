using System.ComponentModel.DataAnnotations;
using IntegracaoSolidesDP.Worker.Erros;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>Tela de erro do usuário: só o código (nada técnico) e, se o perfil puder, o envio ao suporte.</summary>
public sealed class ErroViewModel
{
    /// <summary>E-1027; null quando não deu para registrar.</summary>
    public string? Codigo { get; init; }

    public long? Id { get; init; }

    /// <summary><see cref="ModosEnvioErro"/>.</summary>
    public string ModoEnvio { get; init; } = ModosEnvioErro.Desligado;

    /// <summary>No automático, os erros da Web são avisados sozinhos.</summary>
    public bool AvisoAutomatico { get; init; }

    /// <summary>Mostra o campo "O que você estava fazendo?" e o botão.</summary>
    public bool PodeEnviar { get; init; }

    /// <summary>Depois do envio: a frase de confirmação.</summary>
    public string? Enviado { get; init; }
}

/// <summary>Tela "Erros da aplicação": a lista, com os filtros.</summary>
public sealed class ErrosViewModel
{
    public IReadOnlyList<ErroAplicacao> Erros { get; init; } = [];

    /// <summary>Filtro de situação (null = todas).</summary>
    public string? Situacao { get; init; }

    /// <summary>Filtro de origem (null = as duas).</summary>
    public string? Origem { get; init; }

    /// <summary>O usuário pode silenciar, resolver e reabrir (Admin).</summary>
    public bool PodeAlterar { get; init; }

    /// <summary>Quantos erros a lista mostra no máximo.</summary>
    public int Limite { get; init; }
}

/// <summary>Detalhe de um erro.</summary>
public sealed class ErroDetalheViewModel
{
    public required ErroAplicacao Erro { get; init; }

    public bool PodeAlterar { get; init; }

    /// <summary>O usuário pode pedir o envio ao suporte (envio não desligado e perfil com permissão).</summary>
    public bool PodeEnviar { get; init; }

    /// <summary>Há destinatário ativo com "Recebe erros da aplicação".</summary>
    public bool TemDestinatario { get; init; }
}

/// <summary>Configuração dos erros da aplicação (card na tela Notificações).</summary>
public sealed class ErrosConfigForm
{
    [Display(Name = "Execução que caiu também é erro",
        Description = "Execução abortada (Sólides DP ou banco fora do ar, token inválido) entra na lista de erros. A falha continua avisada em \"Falhas\" de qualquer jeito.")]
    public bool ExecucaoCaida { get; set; } = true;

    [Display(Name = "Operador vê a tela de erros", Description = "Desligado, só o Admin vê. Silenciar, resolver e reabrir são sempre do Admin.")]
    public bool OperadorVe { get; set; }

    [Required(ErrorMessage = "Escolha o envio.")]
    [Display(Name = "Envio de erros ao suporte",
        Description = "Desligado: só registra. Só manual: sai pelo botão Enviar ao suporte. Automático: o primeiro aviso de cada erro na hora e um resumo do que continua acontecendo.")]
    public string ModoEnvio { get; set; } = ModosEnvioErro.Automatico;

    [Display(Name = "Erros da Web", Description = "No automático, os erros das telas também vão para o suporte.")]
    public bool OrigemWeb { get; set; } = true;

    [Display(Name = "Erros do serviço", Description = "No automático, os erros do serviço também vão para o suporte.")]
    public bool OrigemServico { get; set; } = true;

    [Range(1, 168, ErrorMessage = "Informe de 1 a 168 horas.")]
    [Display(Name = "Resumo do mesmo erro a cada (horas)", Description = "Erro já avisado que continua acontecendo: no máximo um resumo neste intervalo.")]
    public int ResumoHoras { get; set; } = 4;

    [Range(1, 500, ErrorMessage = "Informe de 1 a 500.")]
    [Display(Name = "Limite de e-mails de erro por dia", Description = "Ao atingir, sai um último aviso \"limite atingido\" e o resto fica para o dia seguinte.")]
    public int LimiteDia { get; set; } = 20;

    [Display(Name = "Operador pode enviar ao suporte", Description = "Botão Enviar ao suporte para o Operador (na tela de erro e, se ele vê, na tela de erros).")]
    public bool OperadorEnvia { get; set; } = true;

    [StringLength(100, ErrorMessage = "Use até 100 caracteres.")]
    [Display(Name = "Nome do cliente no e-mail", Description = "Vai no assunto, para o suporte saber de qual cliente é o erro. Ex.: ADN.")]
    public string? NomeCliente { get; set; }

    [Range(0, 3650, ErrorMessage = "Informe de 0 a 3650 dias.")]
    [Display(Name = "Guardar erros por (dias)", Description = "Erro que não acontece há mais tempo que isso é apagado na limpeza diária. 0 = nunca apaga.")]
    public int RetencaoDias { get; set; } = 90;

    public static ErrosConfigForm De(ConfiguracaoErros c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new ErrosConfigForm
        {
            ModoEnvio = c.ModoEnvio,
            OrigemWeb = c.OrigemWeb,
            OrigemServico = c.OrigemServico,
            ResumoHoras = c.ResumoHoras,
            LimiteDia = c.LimiteDia,
            OperadorEnvia = c.OperadorEnvia,
            NomeCliente = c.NomeCliente,
            ExecucaoCaida = c.ExecucaoCaida,
            OperadorVe = c.OperadorVe,
            RetencaoDias = c.RetencaoDias,
        };
    }

    /// <summary>A configuração atual com os campos deste card.</summary>
    public ConfiguracaoErros Aplicar(ConfiguracaoErros atual)
    {
        ArgumentNullException.ThrowIfNull(atual);
        return atual with
        {
            ModoEnvio = ModoEnvio is ModosEnvioErro.Desligado or ModosEnvioErro.Manual ? ModoEnvio : ModosEnvioErro.Automatico,
            OrigemWeb = OrigemWeb,
            OrigemServico = OrigemServico,
            ResumoHoras = ResumoHoras,
            LimiteDia = LimiteDia,
            OperadorEnvia = OperadorEnvia,
            NomeCliente = string.IsNullOrWhiteSpace(NomeCliente) ? null : NomeCliente.Trim(),
            ExecucaoCaida = ExecucaoCaida,
            OperadorVe = OperadorVe,
            RetencaoDias = RetencaoDias,
        };
    }
}
