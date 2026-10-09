using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Separa as linhas de func1 de UMA empresa pela identidade do Sólides DP: o CPF.
/// <list type="bullet">
/// <item>Um vínculo ativo (situação que não é demissão, aposentadoria, transferência nem ignorada) = colaborador ativo.
/// Transferência entre filiais: a linha antiga fica 09 e a nova é a ativa, então vira atualização de local.</item>
/// <item>O mesmo CPF ativo em duas matrículas ou duas filiais = pendência (no Sólides DP só existe um CPF ativo por conta).</item>
/// <item>Nenhum vínculo ativo = saída: desligamento (com a causa da rescisão) ou, se só houver linhas 09,
/// transferência para outra empresa. Só é aplicada a quem a integração já vinculou.</item>
/// <item>Ativo numa filial que não está marcada = fora do escopo: nem envia nem desliga.</item>
/// </list>
/// </summary>
public static class EmployeeClassifier
{
    public static EmployeePlan Classify(IReadOnlyList<EmployeeRow> rows, SyncOptions options, DateOnly today, EscopoEmpresa escopo)
    {
        var ignored = options.SituacoesIgnoradas.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var dismissal = options.SituacoesDesligamento.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var transferred = options.SituacaoTransferido.Trim();

        bool IsDismissal(EmployeeRow r) => r.SituacaoDeDesligamento || (r.Situacao is { } s && dismissal.Contains(s));
        bool IsCurrent(EmployeeRow r) => !IsDismissal(r) && r.Situacao != transferred;
        bool InPilot(IEnumerable<EmployeeRow> group, string? cpf) =>
            options.ExternalIdAllowList.Count == 0
            || group.Any(r => InAllowList(options, cpf, r.Nomatric, r.Rotulo));

        var considered = rows
            .Where(r => r.Cdempresa == escopo.Cdempresa)
            .Where(r => r.Situacao is null || !ignored.Contains(r.Situacao))
            .ToList();

        var active = new List<EmployeeRow>();
        var departures = new List<Departure>();
        var skipped = new List<RunItem>();
        var outOfScope = new Dictionary<string, EmployeeRow>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyByMatricula = new Dictionary<string, string>(StringComparer.Ordinal);

        // Sem CPF válido não há identidade: só vira pendência se a linha estiver ativa e em escopo.
        foreach (var row in considered.Where(r => r.Chave is null && IsCurrent(r) && escopo.Inclui(r.Cdfilial) && InPilot([r], null)))
        {
            var (_, warning) = Documents.NormalizeCpf(row.Cpf);
            skipped.Add(new RunItem(EntityTypes.Employee, row.Rotulo, ItemActions.None, ItemStatuses.Skipped,
                Message: $"cpf_invalido: {warning ?? "CPF vazio"}; corrigir func1.nocpf no RHSenso"));
        }

        foreach (var group in considered.Where(r => r.Chave is not null).GroupBy(r => r.Chave!, StringComparer.Ordinal))
        {
            var cpf = group.Key;
            var current = group
                .Where(IsCurrent)
                .OrderByDescending(r => r.DataTransferencia ?? r.DataAdmissao)
                .ThenByDescending(r => r.DataAdmissao)
                .ThenBy(r => r.Id)
                .ToList();

            // Ativas por último: a matrícula atual vence uma antiga reaproveitada.
            foreach (var row in group.OrderBy(r => IsCurrent(r) ? 1 : 0))
            {
                keyByMatricula[row.Nomatric] = cpf;
            }

            labels[cpf] = (current.FirstOrDefault() ?? group.OrderByDescending(r => r.DataAdmissao).First()).Rotulo;

            if (!InPilot(group, cpf))
            {
                continue;
            }

            if (current.Count > 0)
            {
                var vinculos = current.Select(r => (r.Cdfilial, r.Nomatric)).Distinct().ToList();
                if (vinculos.Count > 1)
                {
                    var onde = string.Join(", ", vinculos.Select(v => $"{escopo.Cdempresa}-{v.Nomatric} (filial {v.Cdfilial})"));
                    skipped.Add(new RunItem(EntityTypes.Employee, labels[cpf], ItemActions.None, ItemStatuses.Skipped,
                        Message: $"cpf_ativo_duplicado: o mesmo CPF está ativo em {onde}; corrigir no RHSenso"));
                    continue;
                }

                // Mesma matrícula e filial repetidas (linha duplicada): vale a mais recente.
                var chosen = current[0];
                foreach (var loser in current.Skip(1))
                {
                    skipped.Add(new RunItem(EntityTypes.Employee, labels[cpf], ItemActions.None, ItemStatuses.Skipped,
                        Message: $"superseded: linha duplicada {loser.Cdempresa}-{loser.Cdfilial}-{loser.Nomatric} (id {loser.Id})"));
                }

                if (escopo.Inclui(chosen.Cdfilial))
                {
                    active.Add(chosen);
                }
                else
                {
                    outOfScope[cpf] = chosen;
                }

                continue;
            }

            var dismissed = group.Where(IsDismissal).OrderByDescending(r => r.DataDemissao).FirstOrDefault();
            if (dismissed is not null)
            {
                departures.Add(new Departure(
                    cpf,
                    labels[cpf],
                    dismissed.DataDemissao is { } d ? DateOnly.FromDateTime(d) : today,
                    CodeMaps.ResignationReasonFor(dismissed.CausaRescisao, options.MotivoDemissaoMap),
                    IsTransfer: false));
                continue;
            }

            var transfer = group.OrderByDescending(r => r.DataTransferencia).First();
            departures.Add(new Departure(
                cpf,
                labels[cpf],
                transfer.DataTransferencia is { } t ? DateOnly.FromDateTime(t) : today,
                CodeMaps.ResignationReasonTransfer,
                IsTransfer: true));
        }

        return new EmployeePlan(active, departures, skipped)
        {
            OutOfScope = outOfScope,
            KeyByMatricula = keyByMatricula,
            Labels = labels,
        };
    }

    /// <summary>Piloto (Sync:ExternalIdAllowList): vazio = todos; senão, o CPF, a matrícula ou "{empresa}-{matrícula}".</summary>
    public static bool InAllowList(SyncOptions options, string? cpf, string matricula, string rotulo)
    {
        if (options.ExternalIdAllowList.Count == 0)
        {
            return true;
        }

        var cpfDigits = cpf is null ? null : new string(cpf.Where(char.IsAsciiDigit).ToArray());
        foreach (var raw in options.ExternalIdAllowList)
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (string.Equals(item, matricula.Trim(), StringComparison.Ordinal)
                || string.Equals(item, rotulo, StringComparison.Ordinal)
                || (cpfDigits is not null && string.Equals(new string(item.Where(char.IsAsciiDigit).ToArray()), cpfDigits, StringComparison.Ordinal)
                    && item.Count(char.IsAsciiDigit) == 11))
            {
                return true;
            }
        }

        return false;
    }
}
