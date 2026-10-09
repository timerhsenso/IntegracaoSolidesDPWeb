using System.ComponentModel.DataAnnotations;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Worker.Management;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>Uma linha da tela Empresas: a empresa do RHSenso e como ela está na integração.</summary>
public sealed class LinhaEmpresa
{
    public int Cdempresa { get; init; }
    public string? Nome { get; init; }

    /// <summary>temp1.flativo = 'S'. Empresa configurada que foi inativada na folha fica fora do escopo.</summary>
    public bool AtivaNaFolha { get; init; }

    public EmpresaConfiguracao? Config { get; init; }
    public EmpresaToken? Token { get; init; }
    public VinculosEmpresa? Vinculos { get; init; }
    public Execucao? UltimaExecucao { get; init; }

    public bool Habilitada => Config?.Habilitada == true;
    public bool TemToken => Token?.TokenCifrado is not null;
}

public sealed class EmpresasViewModel
{
    public IReadOnlyList<LinhaEmpresa> Linhas { get; init; } = [];
    public ConfigurationVersion? Configuracao { get; init; }

    /// <summary>Simulação ligada na regra geral (tela Configuração): vale para todas as empresas.</summary>
    public bool DryRunGeral { get; init; }

    /// <summary>Nenhuma empresa configurada ainda: o serviço usa a empresa de "Empresas incluídas" e o token do servidor.</summary>
    public bool ModoUmaEmpresa => Configuracao is null || Configuracao.Empresas.Count == 0;

    /// <summary>Empresa usada enquanto <see cref="ModoUmaEmpresa"/> (campo Empresas incluídas da Configuração).</summary>
    public IReadOnlyList<int> EmpresasIncluidas { get; init; } = [];

    public bool PodeEditar { get; init; }
}

/// <summary>Formulário de uma empresa. Campos vazios usam a regra geral da tela Configuração.</summary>
public sealed class EmpresaForm
{
    public int Cdempresa { get; set; }

    [Display(Name = "Habilitada na integração", Description = "Desligada: a empresa não é sincronizada (nada é enviado e ninguém é desligado).")]
    public bool Habilitada { get; set; }

    [Display(Name = "Modo simulação (dry-run) nesta empresa", Description = "Ligado: só simula. O envio real só acontece com a simulação desligada aqui e na tela Configuração.")]
    public bool DryRun { get; set; } = true;

    [Display(Name = "Data de go-live desta empresa (vazio = a geral)", Description = "Quem for criado pela integração e foi admitido antes começa no Sólides DP nesta data. Quem já existe lá mantém a data de lá.")]
    [DataType(DataType.Date)]
    public DateOnly? GoLiveDate { get; set; }

    [Display(Name = "Escala dos novos colaboradores (externalId; vazio = a geral)", Description = "Usada só na criação. O id aparece em Consultar o Sólides DP desta empresa.")]
    [StringLength(64)]
    public string? WorkScheduleExternalId { get; set; }

    [Display(Name = "Regra de ponto dos novos colaboradores (externalId; vazio = a geral)", Description = "Usada só na criação. O id aparece em Consultar o Sólides DP desta empresa.")]
    [StringLength(64)]
    public string? PunchRuleExternalId { get; set; }

    [Display(Name = "Id do motivo FÉRIAS no Sólides DP (vazio = o geral)", Description = "Cada conta do Sólides DP tem os seus ids. Vazio: a integração procura o motivo FÉRIAS.")]
    public long? FeriasMotivoId { get; set; }

    [Display(Name = "Empresa no Sólides DP", Description = "Como informar a empresa do colaborador nesta conta: pelo CNPJ da filial, nenhuma, ou como na regra geral.")]
    public string? ModoEmpresa { get; set; }

    [Display(Name = "Filiais (nenhuma marcada = todas as filiais ativas)", Description = "Só os colaboradores destas filiais vão para o Sólides DP. Desmarcar uma filial não desliga ninguém.")]
    public List<int> Filiais { get; set; } = [];

    [Required(ErrorMessage = "Descreva o motivo da alteração.")]
    [StringLength(500, ErrorMessage = "Use até 500 caracteres.")]
    [Display(Name = "Motivo da alteração")]
    public string Observacao { get; set; } = string.Empty;

    public static EmpresaForm De(int cdempresa, EmpresaConfiguracao? config) => new()
    {
        Cdempresa = cdempresa,
        Habilitada = config?.Habilitada ?? false,
        DryRun = config?.DryRun ?? true,
        GoLiveDate = config?.GoLiveDate,
        WorkScheduleExternalId = config?.WorkScheduleExternalId,
        PunchRuleExternalId = config?.PunchRuleExternalId,
        FeriasMotivoId = config?.FeriasMotivoId,
        ModoEmpresa = config?.ModoEmpresa,
        Filiais = config?.Filiais.ToList() ?? [],
    };

    public EmpresaConfiguracao ParaConfiguracao() => new()
    {
        Cdempresa = Cdempresa,
        Habilitada = Habilitada,
        DryRun = DryRun,
        GoLiveDate = GoLiveDate,
        WorkScheduleExternalId = Vazio(WorkScheduleExternalId),
        PunchRuleExternalId = Vazio(PunchRuleExternalId),
        FeriasMotivoId = FeriasMotivoId,
        ModoEmpresa = ModoEmpresa is ModosEmpresa.Nenhuma or ModosEmpresa.PorCnpj ? ModoEmpresa : null,
        Filiais = Filiais.Distinct().Order().ToList(),
    };

    private static string? Vazio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}

public sealed class EmpresaEditarViewModel
{
    public required EmpresaForm Form { get; init; }
    public string? Nome { get; init; }
    public bool AtivaNaFolha { get; init; }
    public IReadOnlyList<FilialRhsenso> FiliaisDisponiveis { get; init; } = [];
    public EmpresaToken? Token { get; init; }
    public VinculosEmpresa? Vinculos { get; init; }
    public Execucao? UltimaExecucao { get; init; }
    public bool DryRunGeral { get; init; }
    public DateOnly? GoLiveGeral { get; init; }
    public bool PodeEditar { get; init; }
    public bool PodeOperar { get; init; }

    public bool TemToken => Token?.TokenCifrado is not null;
}
