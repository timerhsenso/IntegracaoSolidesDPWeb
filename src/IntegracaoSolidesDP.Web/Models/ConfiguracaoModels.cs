using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>
/// Regras gerais (seção Sync): as regras da folha, iguais para todas as empresas. O que depende da conta do Sólides DP
/// ou da fase da implantação (simulação, go-live, piloto, filiais, escala, regra, motivo FÉRIAS, empresa no DP) fica na
/// tela Empresas. Listas são digitadas separadas por vírgula ou uma por linha; a conversão devolve os erros de digitação,
/// e as regras de negócio são as do <see cref="SyncOptionsValidator"/> (as mesmas do serviço).
/// </summary>
public sealed class ConfiguracaoForm
{
    [Display(Name = "Tipos de colaborador (func1.tpcolab)", Description = "Códigos de func1.tpcolab que entram: 1 = Empregado, 2 = Estagiário. Separe por vírgula.")]
    public string? TiposColaborador { get; set; } = "1, 2";

    [Display(Name = "Situação de transferência", Description = "Código de func1.cdsituacao que indica transferência (a pessoa continua em outra empresa ou filial).")]
    public string? SituacaoTransferido { get; set; } = "09";

    [Display(Name = "Situações ignoradas", Description = "Códigos de situação que a integração ignora por completo (ex.: 99 = pré-cadastro).")]
    public string? SituacoesIgnoradas { get; set; } = "99";

    [Display(Name = "Motivos de demissão (código=MOTIVO, um por linha; vazio = padrão)", Description = "Só para trocar o padrão: uma linha por código (causa de rescisão do RHSenso = motivo do Sólides DP). Código desconhecido vai como OUTROS.")]
    public string? MotivoDemissaoMap { get; set; }

    [Range(0, 3650, ErrorMessage = "Informe de 0 a 3650 dias.")]
    [Display(Name = "Janela de férias (dias para trás)", Description = "Envia férias que terminaram há no máximo este número de dias (e as futuras).")]
    public int FeriasJanelaDias { get; set; } = 60;

    [Display(Name = "Enviar férias \"Programadas\" como PENDENTE", Description = "Desligado: só envia férias a partir de Liberada. Ligado: as Programadas vão como PENDENTE.")]
    public bool FeriasEnviarProgramadas { get; set; }

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

    [Range(0, 100000, ErrorMessage = "Informe um número não negativo.")]
    [Display(Name = "Máximo de desligamentos por execução", Description = "Trava: se uma execução for desligar mais colaboradores que isto no Sólides DP, nenhum é desligado e fica uma falha em Pendências.")]
    public int MaxDismissalsPerRun { get; set; } = 20;

    [Range(1, 100, ErrorMessage = "Informe de 1 a 100.")]
    [Display(Name = "Recusas do DP por colaborador", Description = "Depois de o Sólides DP recusar este número de vezes seguidas os mesmos dados de um colaborador, a integração para de reenviar a cada execução: tenta de novo quando os dados mudam no RHSenso ou uma vez por dia.")]
    public int ColaboradorMaxTentativas { get; set; } = 3;

    [Range(0, 3650, ErrorMessage = "Informe 0 (não apaga) ou de 30 a 3650.")]
    [Display(Name = "Guardar execuções por (dias)", Description = "Execuções mais antigas que isto (com os itens e relatórios) são apagadas na limpeza diária. 0 = nunca apaga. A última execução de cada empresa fica sempre.")]
    public int RetencaoExecucoesDias { get; set; } = 180;

    [Range(0, 3650, ErrorMessage = "Informe de 0 (não apaga) a 3650.")]
    [Display(Name = "Guardar arquivos de relatório por (dias)", Description = "Arquivos CSV/JSON da pasta de relatórios do serviço mais antigos que isto são apagados. 0 = nunca apaga. O conteúdo continua no banco.")]
    public int RetencaoArquivosDias { get; set; } = 30;

    [Range(0, 23, ErrorMessage = "Informe uma hora de 0 a 23.")]
    [Display(Name = "Hora da limpeza", Description = "A limpeza roda uma vez por dia, a partir desta hora (fuso do serviço).")]
    public int LimpezaHora { get; set; } = 3;

    [Range(1, 1440, ErrorMessage = "Informe de 1 a 1440 minutos.")]
    [Display(Name = "Serviço sem resposta após (minutos)", Description = "O Painel e a tela de pedidos avisam que o serviço parou quando o último sinal de vida dele é mais antigo que isto.")]
    public int ServicoSemRespostaMinutos { get; set; } = 5;

    [Display(Name = "Conferência semanal DP x RHSenso", Description = "Ligado: toda semana o serviço compara os colaboradores vinculados no Sólides DP com o RHSenso (só lê o DP). Desligado: só pelo pedido na Web.")]
    public bool ConferenciaAtiva { get; set; } = true;

    [Display(Name = "Dia da conferência", Description = "Dia da semana em que a conferência DP x RHSenso roda.")]
    public DayOfWeek ConferenciaDiaSemana { get; set; } = DayOfWeek.Monday;

    [Range(0, 23, ErrorMessage = "Informe uma hora de 0 a 23.")]
    [Display(Name = "Hora da conferência", Description = "A conferência roda a partir desta hora do dia escolhido (fuso do serviço).")]
    public int ConferenciaHora { get; set; } = 6;

    [Required(ErrorMessage = "Descreva o motivo da alteração.")]
    [StringLength(500, ErrorMessage = "Use até 500 caracteres.")]
    [Display(Name = "Motivo da alteração")]
    public string Observacao { get; set; } = string.Empty;

    public static ConfiguracaoForm De(SyncOptions options) => new()
    {
        TiposColaborador = string.Join(", ", options.TiposColaborador),
        SituacaoTransferido = options.SituacaoTransferido,
        SituacoesIgnoradas = string.Join(", ", options.SituacoesIgnoradas),
        MotivoDemissaoMap = string.Join(Environment.NewLine, options.MotivoDemissaoMap.Select(p => $"{p.Key}={p.Value}")),
        FeriasJanelaDias = options.FeriasJanelaDias,
        FeriasEnviarProgramadas = options.FeriasEnviarProgramadas,
        FeriasEndDateMode = options.FeriasEndDateMode,
        FeriasMaxTentativas = options.FeriasMaxTentativas,
        MaxCreatesPerRun = options.MaxCreatesPerRun,
        MaxCancellationsPerRun = options.MaxCancellationsPerRun,
        MaxDismissalsPerRun = options.MaxDismissalsPerRun,
        ColaboradorMaxTentativas = options.ColaboradorMaxTentativas,
        RetencaoExecucoesDias = options.RetencaoExecucoesDias,
        RetencaoArquivosDias = options.RetencaoArquivosDias,
        LimpezaHora = options.LimpezaHora,
        ServicoSemRespostaMinutos = options.ServicoSemRespostaMinutos,
        ConferenciaAtiva = options.ConferenciaAtiva,
        ConferenciaDiaSemana = options.ConferenciaDiaSemana,
        ConferenciaHora = options.ConferenciaHora,
    };

    /// <summary>
    /// Converte para a seção Sync a partir da versão em vigor (<paramref name="atual"/>): só as regras gerais mudam.
    /// Erros de digitação vão para <paramref name="erros"/> (chave = campo).
    /// </summary>
    public SyncOptions ParaOpcoes(SyncOptions? atual, IDictionary<string, string> erros)
    {
        // Simulação, go-live, piloto e os dados da conta são da empresa: aqui ficam como estavam e não são usados.
        var options = atual is null ? new SyncOptions() : EmpresaOptions.Copiar(atual, dryRun: true);
        options.DryRun = true;
        options.TiposColaborador = Inteiros(TiposColaborador, nameof(TiposColaborador), erros);
        options.SituacaoTransferido = (SituacaoTransferido ?? string.Empty).Trim();
        options.SituacoesIgnoradas = Itens(SituacoesIgnoradas);
        options.MotivoDemissaoMap = Mapa(MotivoDemissaoMap, nameof(MotivoDemissaoMap), erros);
        options.FeriasJanelaDias = FeriasJanelaDias;
        options.FeriasEnviarProgramadas = FeriasEnviarProgramadas;
        options.FeriasEndDateMode = FeriasEndDateMode;
        options.FeriasMaxTentativas = FeriasMaxTentativas;
        options.MaxCreatesPerRun = MaxCreatesPerRun;
        options.MaxCancellationsPerRun = MaxCancellationsPerRun;
        options.MaxDismissalsPerRun = MaxDismissalsPerRun;
        options.ColaboradorMaxTentativas = ColaboradorMaxTentativas;
        options.RetencaoExecucoesDias = RetencaoExecucoesDias;
        options.RetencaoArquivosDias = RetencaoArquivosDias;
        options.LimpezaHora = LimpezaHora;
        options.ServicoSemRespostaMinutos = ServicoSemRespostaMinutos;
        options.ConferenciaAtiva = ConferenciaAtiva;
        options.ConferenciaDiaSemana = ConferenciaDiaSemana;
        options.ConferenciaHora = ConferenciaHora;
        return options;
    }

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

    /// <summary>Situações que desligam no Sólides DP (fixas; só exibidas).</summary>
    public IReadOnlyList<string> SituacoesDesligamento { get; init; } = new SyncOptions().SituacoesDesligamento.ToList();
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
