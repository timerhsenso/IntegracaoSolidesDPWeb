using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>
/// Formulário da seção Sync. Listas são digitadas separadas por vírgula ou uma por linha;
/// a conversão para <see cref="SyncOptions"/> devolve os erros de digitação, e as regras de
/// negócio são as do <see cref="SyncOptionsValidator"/> (as mesmas do serviço).
/// </summary>
public sealed class ConfiguracaoForm
{
    [Display(Name = "Modo simulação (dry-run): não envia nada ao Sólides DP", Description = "Ligado: só simula e grava o relatório, sem chamar o Sólides DP. Desligue apenas depois do piloto.")]
    public bool DryRun { get; set; } = true;

    [Display(Name = "Data de início no Sólides DP (go-live)", Description = "Obrigatória para o envio real. Quem foi admitido antes começa no Sólides DP nesta data, sem ponto retroativo.")]
    [DataType(DataType.Date)]
    public DateOnly? GoLiveDate { get; set; }

    [Display(Name = "Tipos de colaborador (func1.tpcolab)", Description = "Códigos de func1.tpcolab que entram: 1 = Empregado, 2 = Estagiário. Separe por vírgula.")]
    public string? TiposColaborador { get; set; } = "1, 2";

    [Display(Name = "Empresas incluídas (vazio = todas)", Description = "Códigos de empresa (func1.cdempresa) separados por vírgula. Use para liberar uma empresa de cada vez.")]
    public string? EmpresasIncluidas { get; set; }

    [Display(Name = "Piloto: só estes colaboradores ({empresa}-{matrícula}, um por linha; vazio = todos)", Description = "Para o piloto: só estes colaboradores são enviados, no formato empresa-matrícula (ex.: 14-00901482).")]
    public string? ExternalIdAllowList { get; set; }

    [Display(Name = "Situação de transferência", Description = "Código de func1.cdsituacao que indica transferência (a pessoa continua em outra empresa ou filial).")]
    public string? SituacaoTransferido { get; set; } = "09";

    [Display(Name = "Situações ignoradas", Description = "Códigos de situação que a integração ignora por completo (ex.: 99 = pré-cadastro).")]
    public string? SituacoesIgnoradas { get; set; } = "99";

    [Display(Name = "Escala dos novos colaboradores (externalId; vazio = padrão da conta)", Description = "Usada só na criação do colaborador. O id aparece em Pedidos ao serviço › Consultar o Sólides DP.")]
    public string? WorkScheduleExternalId { get; set; }

    [Display(Name = "Regra de ponto dos novos colaboradores (externalId; vazio = padrão da conta)", Description = "Usada só na criação do colaborador. O id aparece em Pedidos ao serviço › Consultar o Sólides DP.")]
    public string? PunchRuleExternalId { get; set; }

    [Display(Name = "Empresa no Sólides DP", Description = "Como informar a empresa do colaborador: pelo CNPJ da filial, ou nenhuma (conta do DP com uma só empresa).")]
    public CompanyMode CompanyMode { get; set; } = CompanyMode.ResolveByCnpj;

    [Display(Name = "Criar no Sólides DP a empresa (CNPJ) que não existir", Description = "Desligado: colaborador de CNPJ que não existe no DP fica bloqueado até a empresa ser cadastrada lá.")]
    public bool CreateMissingCompanies { get; set; }

    [Display(Name = "Enviar CPFs repetidos como duplo vínculo", Description = "Desligado: matrículas ativas com o mesmo CPF ficam de fora e aparecem em Pendências.")]
    public bool AllowDoubleBind { get; set; }

    [Display(Name = "Motivos de demissão (código=MOTIVO, um por linha; vazio = padrão)", Description = "Só para trocar o padrão: uma linha por código (causa de rescisão do RHSenso = motivo do Sólides DP). Código desconhecido vai como OUTROS.")]
    public string? MotivoDemissaoMap { get; set; }

    [Range(0, 3650, ErrorMessage = "Informe de 0 a 3650 dias.")]
    [Display(Name = "Janela de férias (dias para trás)", Description = "Envia férias que terminaram há no máximo este número de dias (e as futuras).")]
    public int FeriasJanelaDias { get; set; } = 60;

    [Display(Name = "Enviar férias \"Programadas\" como PENDENTE", Description = "Desligado: só envia férias a partir de Liberada. Ligado: as Programadas vão como PENDENTE.")]
    public bool FeriasEnviarProgramadas { get; set; }

    [Display(Name = "Id do motivo FÉRIAS no Sólides DP (vazio = descobrir)", Description = "Vazio: a integração procura o motivo de ajuste chamado FÉRIAS no Sólides DP.")]
    public long? FeriasMotivoId { get; set; }

    [Display(Name = "Cálculo do fim das férias", Description = "Como o fim das férias é informado ao DP. Confira no piloto se o último dia ficou certo.")]
    public FeriasEndDateMode FeriasEndDateMode { get; set; } = FeriasEndDateMode.InicioDoDiaSeguinte;

    [Range(1, 100, ErrorMessage = "Informe de 1 a 100.")]
    [Display(Name = "Tentativas por período de férias", Description = "Depois de falhar este número de vezes, o período só é tentado de novo se mudar no RHSenso.")]
    public int FeriasMaxTentativas { get; set; } = 5;

    [Range(0, 100000, ErrorMessage = "Informe um número não negativo.")]
    [Display(Name = "Máximo de colaboradores novos por execução", Description = "Trava: se uma execução for criar mais colaboradores que isto, nenhum colaborador é enviado e fica uma falha em Pendências.")]
    public int MaxCreatesPerRun { get; set; } = 300;

    [Range(0, 100000, ErrorMessage = "Informe um número não negativo.")]
    [Display(Name = "Máximo de férias canceladas por execução", Description = "Trava: se uma execução for excluir mais férias que isto, nenhuma é excluída e fica uma falha em Pendências.")]
    public int MaxCancellationsPerRun { get; set; } = 20;

    [Required(ErrorMessage = "Descreva o motivo da alteração.")]
    [StringLength(500, ErrorMessage = "Use até 500 caracteres.")]
    [Display(Name = "Motivo da alteração")]
    public string Observacao { get; set; } = string.Empty;

    public static ConfiguracaoForm De(SyncOptions options) => new()
    {
        DryRun = options.DryRun,
        GoLiveDate = options.GoLiveDate,
        TiposColaborador = string.Join(", ", options.TiposColaborador),
        EmpresasIncluidas = string.Join(", ", options.EmpresasIncluidas),
        ExternalIdAllowList = string.Join(Environment.NewLine, options.ExternalIdAllowList),
        SituacaoTransferido = options.SituacaoTransferido,
        SituacoesIgnoradas = string.Join(", ", options.SituacoesIgnoradas),
        WorkScheduleExternalId = options.WorkScheduleExternalId,
        PunchRuleExternalId = options.PunchRuleExternalId,
        CompanyMode = options.CompanyMode,
        CreateMissingCompanies = options.CreateMissingCompanies,
        AllowDoubleBind = options.AllowDoubleBind,
        MotivoDemissaoMap = string.Join(Environment.NewLine, options.MotivoDemissaoMap.Select(p => $"{p.Key}={p.Value}")),
        FeriasJanelaDias = options.FeriasJanelaDias,
        FeriasEnviarProgramadas = options.FeriasEnviarProgramadas,
        FeriasMotivoId = options.FeriasMotivoId,
        FeriasEndDateMode = options.FeriasEndDateMode,
        FeriasMaxTentativas = options.FeriasMaxTentativas,
        MaxCreatesPerRun = options.MaxCreatesPerRun,
        MaxCancellationsPerRun = options.MaxCancellationsPerRun,
    };

    /// <summary>Converte para a seção Sync. Erros de digitação vão para <paramref name="erros"/> (chave = campo).</summary>
    public SyncOptions ParaOpcoes(IDictionary<string, string> erros) => new()
    {
        DryRun = DryRun,
        GoLiveDate = GoLiveDate,
        TiposColaborador = Inteiros(TiposColaborador, nameof(TiposColaborador), erros),
        EmpresasIncluidas = Inteiros(EmpresasIncluidas, nameof(EmpresasIncluidas), erros),
        ExternalIdAllowList = Itens(ExternalIdAllowList),
        SituacaoTransferido = (SituacaoTransferido ?? string.Empty).Trim(),
        SituacoesIgnoradas = Itens(SituacoesIgnoradas),
        WorkScheduleExternalId = (WorkScheduleExternalId ?? string.Empty).Trim(),
        PunchRuleExternalId = (PunchRuleExternalId ?? string.Empty).Trim(),
        CompanyMode = CompanyMode,
        CreateMissingCompanies = CreateMissingCompanies,
        AllowDoubleBind = AllowDoubleBind,
        MotivoDemissaoMap = Mapa(MotivoDemissaoMap, nameof(MotivoDemissaoMap), erros),
        FeriasJanelaDias = FeriasJanelaDias,
        FeriasEnviarProgramadas = FeriasEnviarProgramadas,
        FeriasMotivoId = FeriasMotivoId,
        FeriasEndDateMode = FeriasEndDateMode,
        FeriasMaxTentativas = FeriasMaxTentativas,
        MaxCreatesPerRun = MaxCreatesPerRun,
        MaxCancellationsPerRun = MaxCancellationsPerRun,
    };

    private static readonly char[] Separadores = [',', ';', '\r', '\n', ' ', '\t'];
    private static readonly char[] Linhas = ['\r', '\n'];

    private static List<string> Itens(string? texto) =>
        (texto ?? string.Empty).Split(Separadores, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private static List<int> Inteiros(string? texto, string campo, IDictionary<string, string> erros)
    {
        var numeros = new List<int>();
        foreach (var item in Itens(texto))
        {
            if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero))
            {
                numeros.Add(numero);
            }
            else
            {
                erros[campo] = $"\"{item}\" não é um número.";
            }
        }

        return numeros;
    }

    private static Dictionary<string, string> Mapa(string? texto, string campo, IDictionary<string, string> erros)
    {
        var mapa = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var linha in (texto ?? string.Empty).Split(Linhas, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var partes = linha.Split('=', 2, StringSplitOptions.TrimEntries);
            if (partes.Length != 2 || partes[0].Length == 0 || partes[1].Length == 0)
            {
                erros[campo] = $"Linha \"{linha}\" fora do formato código=MOTIVO.";
                continue;
            }

            mapa[partes[0]] = partes[1].ToUpperInvariant();
        }

        return mapa;
    }
}

public sealed class ConfiguracaoViewModel
{
    public ConfigurationVersion? Atual { get; init; }
    public required ConfiguracaoForm Form { get; init; }
    public bool PodeEditar { get; init; }
    public bool GestaoPreparada { get; init; }
}

/// <summary>Uma versão e o que mudou em relação à anterior.</summary>
public sealed class VersaoViewModel
{
    public required VersaoConfiguracao Versao { get; init; }
    public VersaoConfiguracao? Anterior { get; init; }
    public IReadOnlyList<Diferenca> Diferencas { get; init; } = [];

    public static IReadOnlyList<Diferenca> Comparar(VersaoConfiguracao versao, VersaoConfiguracao? anterior)
    {
        var diferencas = new List<Diferenca>();
        if (anterior is not null && anterior.Ativo != versao.Ativo)
        {
            diferencas.Add(new Diferenca("Integração", anterior.Ativo ? "ativa" : "desativada", versao.Ativo ? "ativa" : "desativada"));
        }

        var atual = Objeto(versao.SyncJson);
        var antes = anterior is null ? new JsonObject() : Objeto(anterior.SyncJson);
        foreach (var chave in atual.Select(p => p.Key).Union(antes.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var depois = atual[chave]?.ToJsonString() ?? "—";
            var valorAntes = antes[chave]?.ToJsonString() ?? "—";
            if (!string.Equals(depois, valorAntes, StringComparison.Ordinal))
            {
                diferencas.Add(new Diferenca(chave, anterior is null ? "—" : valorAntes, depois));
            }
        }

        return diferencas;
    }

    private static JsonObject Objeto(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}

public sealed record Diferenca(string Campo, string Antes, string Depois);
