using System.ComponentModel.DataAnnotations;
using IntegracaoSolidesDP.Worker.Erros;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>Tela de erro do usuário: só o código (nada técnico).</summary>
/// <param name="Codigo">E-1027; null quando não deu para registrar.</param>
public sealed record ErroViewModel(string? Codigo);

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
}

/// <summary>Configuração dos erros da aplicação (card na tela Notificações).</summary>
public sealed class ErrosConfigForm
{
    [Display(Name = "Execução que caiu também é erro",
        Description = "Execução abortada (Sólides DP ou banco fora do ar, token inválido) entra na lista de erros. A falha continua avisada em \"Falhas\" de qualquer jeito.")]
    public bool ExecucaoCaida { get; set; } = true;

    [Display(Name = "Operador vê a tela de erros", Description = "Desligado, só o Admin vê. Silenciar, resolver e reabrir são sempre do Admin.")]
    public bool OperadorVe { get; set; }

    [Range(0, 3650, ErrorMessage = "Informe de 0 a 3650 dias.")]
    [Display(Name = "Guardar erros por (dias)", Description = "Erro que não acontece há mais tempo que isso é apagado na limpeza diária. 0 = nunca apaga.")]
    public int RetencaoDias { get; set; } = 90;

    public static ErrosConfigForm De(ConfiguracaoErros c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new ErrosConfigForm { ExecucaoCaida = c.ExecucaoCaida, OperadorVe = c.OperadorVe, RetencaoDias = c.RetencaoDias };
    }

    /// <summary>A configuração atual com os campos deste card (os demais ficam como estão).</summary>
    public ConfiguracaoErros Aplicar(ConfiguracaoErros atual)
    {
        ArgumentNullException.ThrowIfNull(atual);
        return atual with { ExecucaoCaida = ExecucaoCaida, OperadorVe = OperadorVe, RetencaoDias = RetencaoDias };
    }
}
