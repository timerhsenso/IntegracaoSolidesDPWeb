using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Worker.Management;

namespace IntegracaoSolidesDP.Web.Models;

public sealed class PainelViewModel
{
    public bool GestaoPreparada { get; init; }
    public ConfigurationVersion? Configuracao { get; init; }
    public bool? DryRunConfigurado { get; init; }
    public Execucao? EmAndamento { get; init; }
    public Execucao? UltimaExecucao { get; init; }
    public Execucao? UltimaExecucaoReal { get; init; }
    public int PendenciasUltimaReal { get; init; }
    public IReadOnlyList<Contagem> Totais { get; init; } = [];
    public IReadOnlyList<ExecucoesDoDia> PorDia { get; init; } = [];
    public IReadOnlyList<Comando> ComandosRecentes { get; init; } = [];

    /// <summary>Pedido esperando o serviço há mais de 2 minutos: serviço parado ou com a gestão desligada.</summary>
    public DateTimeOffset? PedidoParadoDesde { get; init; }

    public int Total(string tipo, params string[] status) =>
        Totais.Where(t => t.Tipo == tipo && (status.Length == 0 || status.Contains(t.Status))).Sum(t => t.Quantidade);
}

public sealed record ExecucoesDoDia(DateOnly Dia, int Concluidas, int ComErros, int Falhas)
{
    public int Total => Concluidas + ComErros + Falhas;
}

public sealed class ExecucaoDetalheViewModel
{
    public required Execucao Execucao { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Contagens { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();
}

public sealed record MigradosViewModel(string Tipo);

public sealed class HistoricoViewModel
{
    public required string Tipo { get; init; }
    public required string ExternalId { get; init; }
    public string? Nome { get; init; }
    public IReadOnlyList<HistoricoItem> Itens { get; init; } = [];
}

/// <summary>Grade de itens de uma execução; com <see cref="SomentePendencias"/>, só os status que pedem ação.</summary>
public sealed record ItensExecucaoModel(Guid RunId, bool SomentePendencias)
{
    private static readonly string[] TodosStatus =
        ["created", "updated", "adopted", "dismissed", "cancelled", "dry_run", "skipped", "deferred", "blocked", "warning", "failed"];

    public IReadOnlyList<string> StatusDisponiveis => SomentePendencias ? PainelRepository.StatusPendencia : TodosStatus;
}
