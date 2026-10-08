using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Agrupa as linhas de func1 pela chave do DP ("{empresa}-{matrícula}"). Uma transferência
/// entre filiais da mesma empresa cria uma linha nova com a mesma matrícula e deixa a antiga
/// como 09: a chave continua ativa e vira um update de local. Uma transferência para outra
/// empresa deixa a chave antiga só com linhas 09: vira desligamento por transferência.
/// </summary>
public static class EmployeeClassifier
{
    public static EmployeePlan Classify(IReadOnlyList<EmployeeRow> rows, SyncOptions options, DateOnly today)
    {
        var allowList = options.ExternalIdAllowList.Count == 0
            ? null
            : options.ExternalIdAllowList.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var ignored = options.SituacoesIgnoradas.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);

        var active = new List<EmployeeRow>();
        var departures = new List<Departure>();
        var skipped = new List<RunItem>();

        var groups = rows
            .Where(r => r.Situacao is null || !ignored.Contains(r.Situacao))
            .Where(r => allowList is null || allowList.Contains(r.ExternalId))
            .GroupBy(r => r.ExternalId, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var current = group
                .Where(r => !r.SituacaoDeDesligamento && r.Situacao != options.SituacaoTransferido)
                .OrderByDescending(r => r.DataTransferencia ?? r.DataAdmissao)
                .ThenByDescending(r => r.DataAdmissao)
                .ThenBy(r => r.Id)
                .ToList();

            if (current.Count > 0)
            {
                active.Add(current[0]);
                foreach (var loser in current.Skip(1))
                {
                    skipped.Add(new RunItem(EntityTypes.Employee, group.Key, ItemActions.None, ItemStatuses.Skipped,
                        Message: $"superseded: linha duplicada {loser.Cdempresa}-{loser.Cdfilial}-{loser.Nomatric} (id {loser.Id})"));
                }

                continue;
            }

            var dismissal = group.Where(r => r.SituacaoDeDesligamento).OrderByDescending(r => r.DataDemissao).FirstOrDefault();
            if (dismissal is not null)
            {
                departures.Add(new Departure(
                    group.Key,
                    dismissal.DataDemissao is { } d ? DateOnly.FromDateTime(d) : today,
                    CodeMaps.ResignationReasonFor(dismissal.CausaRescisao, options.MotivoDemissaoMap),
                    IsTransfer: false));
                continue;
            }

            var transfer = group.OrderByDescending(r => r.DataTransferencia).First();
            departures.Add(new Departure(
                group.Key,
                transfer.DataTransferencia is { } t ? DateOnly.FromDateTime(t) : today,
                CodeMaps.ResignationReasonTransfer,
                IsTransfer: true));
        }

        // CPF repetido entre ativos: o DP provavelmente recusa (ou cria duplo vínculo). Fica de fora,
        // salvo Sync:AllowDoubleBind, para alguém decidir caso a caso.
        var doubleBind = new HashSet<string>(StringComparer.Ordinal);
        var duplicatedCpfs = active
            .Select(r => (Row: r, Cpf: Documents.NormalizeCpf(r.Cpf).Value))
            .Where(x => x.Cpf is not null)
            .GroupBy(x => x.Cpf!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicatedCpfs)
        {
            var keys = group.Select(x => x.Row.ExternalId).ToList();
            if (options.AllowDoubleBind)
            {
                doubleBind.UnionWith(keys);
                continue;
            }

            foreach (var key in keys)
            {
                skipped.Add(new RunItem(EntityTypes.Employee, key, ItemActions.None, ItemStatuses.Skipped,
                    Message: $"skipped_duplicate_cpf: mesmo CPF em {string.Join(", ", keys)}"));
            }

            active.RemoveAll(r => keys.Contains(r.ExternalId, StringComparer.Ordinal));
        }

        return new EmployeePlan(active, departures, doubleBind, skipped);
    }
}
