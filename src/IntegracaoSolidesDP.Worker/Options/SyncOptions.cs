namespace IntegracaoSolidesDP.Worker.Options;

/// <summary>Regras de negócio da sincronização RHSenso → Sólides DP.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Nome da instância; separa o lock de execução quando há mais de uma conta/token.</summary>
    public string InstanceName { get; set; } = "default";

    /// <summary>Simula tudo sem nenhuma chamada à API (nem leitura). Padrão seguro.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>func1.tpcolab sincronizados. 1 = Empregado, 2 = Estagiário.</summary>
    public IList<int> TiposColaborador { get; set; } = [1, 2];

    /// <summary>Restringe a empresas (func1.cdempresa). Vazio = todas.</summary>
    public IList<int> EmpresasIncluidas { get; set; } = [];

    /// <summary>Restringe a colaboradores específicos (externalId "{empresa}-{matricula}"). Vazio = todos. Usado no piloto.</summary>
    public IList<string> ExternalIdAllowList { get; set; } = [];

    /// <summary>func1.cdsituacao de transferência (a pessoa continua em outra empresa/filial).</summary>
    public string SituacaoTransferido { get; set; } = "09";

    /// <summary>func1.cdsituacao ignorados por completo (ex.: 99 = pré-cadastro).</summary>
    public IList<string> SituacoesIgnoradas { get; set; } = ["99"];

    /// <summary>
    /// Data de início do uso do Sólides DP. Vira o <c>effectiveDate</c> (e as datas de escala/regra)
    /// de quem foi admitido antes, para o DP não calcular ponto retroativo. Obrigatória fora do dry-run.
    /// </summary>
    public DateOnly? GoLiveDate { get; set; }

    /// <summary>externalId da escala atribuída na criação. Vazio = escala padrão da conta.</summary>
    public string WorkScheduleExternalId { get; set; } = string.Empty;

    /// <summary>externalId da regra de ponto atribuída na criação. Vazio = regra padrão da conta.</summary>
    public string PunchRuleExternalId { get; set; } = string.Empty;

    /// <summary>Como resolver a empresa (CNPJ) do colaborador no DP.</summary>
    public CompanyMode CompanyMode { get; set; } = CompanyMode.ResolveByCnpj;

    /// <summary>Cria no DP a empresa cujo CNPJ não existir. Desligado: empresa é entidade legal/eSocial.</summary>
    public bool CreateMissingCompanies { get; set; }

    /// <summary>Envia colaboradores com CPF repetido entre os ativos (duplo vínculo). Desligado: ficam de fora no relatório.</summary>
    public bool AllowDoubleBind { get; set; }

    /// <summary>func1.cdcausres → resignationReason do DP. Código ausente vira OUTROS.</summary>
    public IDictionary<string, string> MotivoDemissaoMap { get; set; } = new Dictionary<string, string>();

    /// <summary>Envia férias com dtfimpf a partir de hoje menos esta janela.</summary>
    public int FeriasJanelaDias { get; set; } = 60;

    /// <summary>Envia férias "Programadas" (feria2.flconfirm = 1) como PENDENTE. Desligado: só a partir de "Liberada".</summary>
    public bool FeriasEnviarProgramadas { get; set; }

    /// <summary>Id do motivo de ajuste FÉRIAS no DP. Vazio = descobre por descrição.</summary>
    public long? FeriasMotivoId { get; set; }

    /// <summary>Como o endDate das férias é calculado a partir do último dia de gozo.</summary>
    public FeriasEndDateMode FeriasEndDateMode { get; set; } = FeriasEndDateMode.InicioDoDiaSeguinte;

    /// <summary>Tentativas de envio de um mesmo período de férias antes de desistir até ele mudar.</summary>
    public int FeriasMaxTentativas { get; set; } = 5;

    /// <summary>Trava: aborta a etapa se for criar mais colaboradores que isto numa execução.</summary>
    public int MaxCreatesPerRun { get; set; } = 300;

    /// <summary>Trava: não cancela mais férias que isto numa execução.</summary>
    public int MaxCancellationsPerRun { get; set; } = 20;

    /// <summary>Pasta dos relatórios CSV/JSON de cada execução (relativa ao executável).</summary>
    public string ReportDirectory { get; set; } = "reports";
}

public enum CompanyMode
{
    /// <summary>Procura a empresa no DP pelo CNPJ da filial (test1.cdcgc). Sem correspondência, o colaborador fica bloqueado.</summary>
    ResolveByCnpj,

    /// <summary>Não envia empresa (conta do DP com uma só empresa).</summary>
    None,
}

public enum FeriasEndDateMode
{
    /// <summary>00:00 do dia seguinte ao último dia (como no exemplo da documentação: 01/06 → 01/07 para 30 dias).</summary>
    InicioDoDiaSeguinte,

    /// <summary>00:00 do último dia de gozo.</summary>
    InicioDoUltimoDia,

    /// <summary>23:59:59.999 do último dia de gozo.</summary>
    FimDoUltimoDia,
}
