using System.Globalization;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline.Steps;

/// <summary>
/// Desligamentos e transferências para outra empresa, sempre por um fato do RHSenso (situação de demissão
/// ou aposentadoria, ou todas as linhas 09). Roda antes dos colaboradores. Só toca quem tem vínculo na conta
/// desta empresa (criado ou vinculado pelo CPF): os desligados históricos nunca são enviados, e mudança de
/// configuração (filial desmarcada, empresa inativa) nunca desliga ninguém.
/// Trava: mais desligamentos que Sync:MaxDismissalsPerRun numa execução real = nenhum é enviado.
/// Na simulação, cada desligamento vira uma linha "Desligar" do relatório de vinculação.
/// </summary>
public sealed class DismissalStep(ISolidesDpClient api, IStateStore state, EpochDates dates, SyncOptionsAccessor syncOptions)
{
    private SyncOptions Options => syncOptions.Current;

    public async Task ExecuteAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        var states = await state.LoadEntityStatesAsync(context.Cdempresa, EntityTypes.Employee, ct);
        var due = new List<(Departure Departure, EntityState Current, long RemoteId)>();
        foreach (var departure in plan.Departures.OrderBy(d => d.Date))
        {
            if (!states.TryGetValue(departure.Key, out var current)
                || current.RemoteId is not { } remoteId
                || current.Status == EntityStatuses.Dismissed)
            {
                continue;
            }

            if (departure.Date > context.Today)
            {
                context.Add(new RunItem(EntityTypes.Employee, departure.Label, ItemActions.Dismiss, ItemStatuses.Deferred,
                    Message: $"desligamento em {departure.Date:yyyy-MM-dd}: {Reason(departure)}"));
                Vinculacao(context, plan, departure, remoteId, "agendado: a integração desliga no Sólides DP nesta data");
                continue;
            }

            due.Add((departure, current, remoteId));
        }

        if (due.Count > Options.MaxDismissalsPerRun)
        {
            var message = FormattableString.Invariant(
                $"max_dismissals_exceeded: {due.Count} desligamentos > Sync:MaxDismissalsPerRun ({Options.MaxDismissalsPerRun}).");
            if (!context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.Dismiss, ItemStatuses.Failed,
                    Message: message + " Nenhum foi enviado: confira as demissões no RHSenso e, se estiverem certas, aumente o limite."));
                return;
            }

            context.Add(new RunItem(EntityTypes.Employee, "-", ItemActions.Dismiss, ItemStatuses.Warning,
                Message: message + " Na execução real, nenhum seria enviado."));
        }

        foreach (var (departure, current, remoteId) in due)
        {
            var label = departure.Label;
            var reason = Reason(departure);
            if (context.DryRun)
            {
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.DryRun,
                    Message: $"{departure.Date:yyyy-MM-dd}: {reason} (cadastro {remoteId} do DP)"));
                Vinculacao(context, plan, departure, remoteId, null);
                continue;
            }

            var result = await api.DismissEmployeeAsync(
                new DismissRequest(remoteId, dates.StartOfDay(departure.Date), departure.Reason), ct);
            ReferenceResolver.ThrowIfUnauthorized(result);
            if (result.IsSuccess)
            {
                await state.UpsertEntityStateAsync(context.Cdempresa, current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"{departure.Date:yyyy-MM-dd}: {reason}"));
                continue;
            }

            // Um retry pode ter desligado e a resposta se perdido: confere antes de reportar falha.
            var check = await api.FindEmployeeByIdAsync(remoteId, ct);
            if (check is { IsSuccess: true, Value.Fired: true })
            {
                await state.UpsertEntityStateAsync(context.Cdempresa, current with { Status = EntityStatuses.Dismissed }, ct);
                context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Dismissed,
                    result.HttpStatus, $"já estava desligado no DP ({result.Message})"));
                continue;
            }

            context.Add(new RunItem(EntityTypes.Employee, label, ItemActions.Dismiss, ItemStatuses.Failed,
                result.HttpStatus, result.Message));
        }
    }

    private static string Reason(Departure departure) =>
        departure.IsTransfer ? $"transferência ({departure.Reason})" : departure.Reason;

    /// <summary>Linha "Desligar" do relatório de vinculação (só na simulação).</summary>
    private static void Vinculacao(SyncContext context, EmployeePlan plan, Departure departure, long remoteId, string? nota)
    {
        if (!context.DryRun)
        {
            return;
        }

        var quando = departure.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var fato = departure.IsTransfer
            ? $"transferido para outra empresa no RHSenso em {quando}"
            : $"desligado no RHSenso em {quando} ({departure.Reason})";
        context.Vinculacao.Add(new VinculacaoLinha
        {
            Rotulo = departure.Label,
            Nome = plan.NomesPorRotulo.GetValueOrDefault(departure.Label),
            Cpf = departure.Key,
            Resultado = ResultadosVinculacao.Desligar,
            CadastroDp = remoteId,
            Observacao = nota is null ? fato : $"{fato}; {nota}",
        });
    }
}
