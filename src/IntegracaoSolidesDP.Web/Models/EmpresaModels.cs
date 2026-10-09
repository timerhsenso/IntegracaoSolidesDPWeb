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

    /// <summary>Integração desativada no Painel (chave geral): nenhuma empresa envia de verdade.</summary>
    public bool Desativada => Configuracao is { Active: false };

    /// <summary>Nenhuma empresa habilitada: o serviço não sincroniza nada.</summary>
    public bool NenhumaHabilitada => Linhas.All(l => !l.Habilitada);

    public bool PodeEditar { get; init; }
}

/// <summary>
/// Formulário de uma empresa: tudo o que depende da conta do Sólides DP ou da fase da implantação. As regras da folha
/// (tipos, situações, férias, travas) são gerais, na tela Regras gerais.
/// </summary>
public sealed class EmpresaForm
{
    public int Cdempresa { get; set; }

    [Display(Name = "Habilitada na integração", Description = "Desligada: a empresa não é sincronizada (nada é enviado e ninguém é desligado).")]
    public bool Habilitada { get; set; }

    [Display(Name = "Modo simulação (dry-run)", Description = "Ligado: só simula, nada é gravado no Sólides DP (com token, só consulta). Desligue só depois do piloto.")]
    public bool DryRun { get; set; } = true;

    [Display(Name = "Data de go-live", Description = "Obrigatória para o envio real. Quem a integração cria e foi admitido antes começa no Sólides DP nesta data; quem já existe lá mantém a data de lá.")]
    [DataType(DataType.Date)]
    public DateOnly? GoLiveDate { get; set; }

    [Display(Name = "Escala dos novos colaboradores (externalId; vazio = padrão da conta)", Description = "Usada só na criação. O id aparece em Consultar o Sólides DP desta empresa.")]
    [StringLength(64)]
    public string? WorkScheduleExternalId { get; set; }

    [Display(Name = "Regra de ponto dos novos colaboradores (externalId; vazio = padrão da conta)", Description = "Usada só na criação. O id aparece em Consultar o Sólides DP desta empresa.")]
    [StringLength(64)]
    public string? PunchRuleExternalId { get; set; }

    [Display(Name = "Id do motivo FÉRIAS no Sólides DP (vazio = descobrir)", Description = "Cada conta do Sólides DP tem os seus ids. Vazio: a integração procura o motivo de ajuste chamado FÉRIAS.")]
    public long? FeriasMotivoId { get; set; }

    [Display(Name = "Empresa no Sólides DP", Description = "Como informar a empresa do colaborador nesta conta: pelo CNPJ da filial, ou nenhuma (conta com uma só empresa).")]
    public string? ModoEmpresa { get; set; } = ModosEmpresa.PorCnpj;

    [Display(Name = "Criar no Sólides DP a empresa (CNPJ) que não existir", Description = "Desligado: colaborador de filial cujo CNPJ não existe na conta fica bloqueado até a empresa ser cadastrada lá.")]
    public bool CriarEmpresasFaltantes { get; set; }

    [Display(Name = "Piloto: só estes colaboradores (um por linha; vazio = todos)", Description = "Só estas pessoas são sincronizadas (inclusive desligamentos e férias). Use o CPF, a matrícula ou empresa-matrícula (ex.: 15-00007811).")]
    [StringLength(4000)]
    public string? Piloto { get; set; }

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
        ModoEmpresa = config?.ModoEmpresa ?? ModosEmpresa.PorCnpj,
        CriarEmpresasFaltantes = config?.CriarEmpresasFaltantes ?? false,
        Filiais = config?.Filiais.ToList() ?? [],
        Piloto = string.Join(Environment.NewLine, config?.Piloto ?? []),
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
        ModoEmpresa = ModoEmpresa == ModosEmpresa.Nenhuma ? ModosEmpresa.Nenhuma : ModosEmpresa.PorCnpj,
        CriarEmpresasFaltantes = CriarEmpresasFaltantes,
        Filiais = Filiais.Distinct().Order().ToList(),
        Piloto = ItensPiloto(Piloto),
    };

    /// <summary>Uma pessoa por linha (também aceita vírgula ou ponto e vírgula).</summary>
    public static List<string> ItensPiloto(string? texto) =>
        (texto ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(i => i.Length <= 32)
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
    public bool IntegracaoDesativada { get; init; }
    public bool PodeEditar { get; init; }
    public bool PodeOperar { get; init; }

    public bool TemToken => Token?.TokenCifrado is not null;
}
