using System.Globalization;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>Alguém ativo no Sólides DP sem colaborador ativo correspondente no RHSenso, com o que o RHSenso diz dele.</summary>
/// <param name="Saida">A saída (desligamento ou transferência) do CPF nesta empresa, quando há.</param>
/// <param name="Motivo">Texto para o RH: o que o RHSenso diz do CPF e o que fazer.</param>
public sealed record SoNoDpCaso(EmployeeDto Dp, string? Cpf, Departure? Saida, string Motivo);

/// <summary>
/// "Só no Sólides DP": quem está ATIVO na conta do DP da empresa sem colaborador ativo no RHSenso com o mesmo CPF. Usado
/// pelo relatório de vinculação da simulação e pela conferência DP x RHSenso. A integração não mexe nesses cadastros: só
/// desliga quem ela vinculou. Quem foi vinculado e saiu do RHSenso não entra aqui (o desligamento é da integração).
/// </summary>
public static class SoNoDpAnalise
{
    /// <param name="ativosDp">Os ativos da conta no DP (find-all).</param>
    /// <param name="vinculos">Vínculos de colaborador da empresa, por CPF.</param>
    public static async Task<IReadOnlyList<SoNoDpCaso>> AnalisarAsync(
        IEnumerable<EmployeeDto> ativosDp,
        EmployeePlan plan,
        IReadOnlyDictionary<string, EntityState> vinculos,
        SyncOptions options,
        int cdempresa,
        DateOnly hoje,
        ISourceReader source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ativosDp);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(vinculos);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        var saidas = plan.Departures.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var soNoDp = new List<(EmployeeDto Dp, string? Cpf, Departure? Saida)>();
        foreach (var dp in ativosDp)
        {
            var cpf = Documents.NormalizeCpf(dp.Cpf).Value;
            if (cpf is not null && plan.CpfsAtivosNoRhSenso.Contains(cpf))
            {
                continue;
            }

            Departure? saida = null;
            if (cpf is not null && saidas.TryGetValue(cpf, out var encontrada))
            {
                // Já vinculado: o DismissalStep desliga (linha "Desligar"); não é decisão do RH.
                if (vinculos.GetValueOrDefault(cpf) is { RemoteId: not null } vinculo && vinculo.Status != EntityStatuses.Dismissed)
                {
                    continue;
                }

                saida = encontrada;
            }

            soNoDp.Add((dp, cpf, saida));
        }

        // Sem saída nesta empresa: procura o CPF no func1 inteiro (outras empresas, tipos e situações) para dizer onde está.
        var procurar = soNoDp.Where(s => s.Cpf is not null && s.Saida is null).Select(s => s.Cpf!).Distinct(StringComparer.Ordinal).ToList();
        var noRhSenso = (await source.ReadEmployeesByCpfAsync(procurar, ct))
            .Where(r => r.Chave is not null)
            .GroupBy(r => r.Chave!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EmployeeRow>)g.ToList(), StringComparer.Ordinal);

        return soNoDp
            .Select(s => new SoNoDpCaso(s.Dp, s.Cpf, s.Saida,
                Motivo(options, plan, cdempresa, hoje, s.Cpf, s.Saida, s.Dp, s.Cpf is null ? null : noRhSenso.GetValueOrDefault(s.Cpf))))
            .ToList();
    }

    /// <summary>
    /// O que o RHSenso diz do CPF de quem está ativo só no Sólides DP. <c>linhas</c> = linhas de func1 com este CPF em
    /// qualquer empresa, tipo ou situação (null quando não houve busca).
    /// </summary>
    private static string Motivo(
        SyncOptions options, EmployeePlan plan, int cdempresa, DateOnly hoje, string? cpf, Departure? saida, EmployeeDto dp, IReadOnlyList<EmployeeRow>? linhas)
    {
        if (cpf is null)
        {
            return "CPF vazio ou inválido no Sólides DP: não dá para comparar com o RHSenso; corrigir o CPF no DP";
        }

        if (saida is not null)
        {
            var quando = saida.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var futuro = saida.Date > hoje;
            return saida.IsTransfer
                ? $"transferido para outra empresa no RHSenso em {quando}; desligar nesta conta do Sólides DP" +
                  (futuro ? " nessa data" : string.Empty) + " (na conta da nova empresa a pessoa é vinculada pelo CPF)"
                : $"desligado no RHSenso em {quando} ({saida.Reason}); a integração só desliga quem ela vinculou: desligar no Sólides DP" +
                  (futuro ? " nessa data" : string.Empty);
        }

        if (linhas is { Count: > 0 })
        {
            return OndeEstaNoRhSenso(options, linhas);
        }

        if (EmployeeMapper.Clean(dp.ExternalId) is { } codigo && MatriculaComCodigo(plan, codigo) is { } matricula)
        {
            return FormattableString.Invariant(
                $"o Código Externo {codigo} é a matrícula {cdempresa}-{matricula} do RHSenso, que tem outro CPF: conferir o CPF no Sólides DP ou no RHSenso");
        }

        return "CPF não existe no RHSenso (em nenhuma empresa, tipo de colaborador ou situação): cadastro feito só no Sólides DP; " +
               "conferir se a pessoa deve continuar no ponto";
    }

    /// <summary>
    /// "no RHSenso: empresa 1, matrícula 00001112: desligado em 18/01/2007; ..." — vínculos ativos primeiro, depois os mais recentes;
    /// no máximo três.
    /// </summary>
    private static string OndeEstaNoRhSenso(SyncOptions options, IReadOnlyList<EmployeeRow> linhas)
    {
        var tipos = options.TiposColaborador.ToHashSet();
        var ignoradas = options.SituacoesIgnoradas.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var desligamento = options.SituacoesDesligamento.Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        var transferido = options.SituacaoTransferido.Trim();

        var descricoes = linhas
            .Select(r =>
            {
                var desligada = r.SituacaoDeDesligamento || (r.Situacao is { } s && desligamento.Contains(s));
                var transferida = !desligada && r.Situacao == transferido;
                return (Row: r, Desligada: desligada, Transferida: transferida);
            })
            .OrderBy(x => x.Desligada || x.Transferida ? 1 : 0)
            .ThenByDescending(x => x.Row.DataDemissao ?? x.Row.DataTransferencia ?? x.Row.DataAdmissao)
            .Select(x =>
            {
                var r = x.Row;
                var onde = FormattableString.Invariant($"empresa {r.Cdempresa}, matrícula {r.Nomatric.Trim()}");
                if (x.Desligada)
                {
                    return $"{onde}: desligado em {Dia(r.DataDemissao)}";
                }

                if (x.Transferida)
                {
                    return $"{onde}: transferido em {Dia(r.DataTransferencia)}";
                }

                if (r.Situacao is { } situacao && ignoradas.Contains(situacao))
                {
                    return $"{onde}: situação {situacao}, ignorada pela integração";
                }

                return tipos.Contains(r.TipoColaborador)
                    ? $"{onde}: ativo"
                    : FormattableString.Invariant($"{onde}: ativo, tipo de colaborador {r.TipoColaborador}, fora dos tipos da integração");
            })
            .ToList();

        var texto = string.Join("; ", descricoes.Take(3));
        if (descricoes.Count > 3)
        {
            texto += FormattableString.Invariant($"; e mais {descricoes.Count - 3}");
        }

        return $"no RHSenso: {texto}. Conferir se a pessoa deve continuar no ponto desta conta";
    }

    private static string Dia(DateTime? data) =>
        data is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "data não informada";

    /// <summary>Matrícula do RHSenso igual ao Código Externo do DP, também sem os zeros à esquerda ("7811" = "00007811").</summary>
    private static string? MatriculaComCodigo(EmployeePlan plan, string codigo)
    {
        if (plan.KeyByMatricula.ContainsKey(codigo))
        {
            return codigo;
        }

        if (!codigo.All(char.IsAsciiDigit))
        {
            return null;
        }

        var semZeros = codigo.TrimStart('0');
        return plan.KeyByMatricula.Keys.FirstOrDefault(m =>
            m.All(char.IsAsciiDigit) && string.Equals(m.TrimStart('0'), semZeros, StringComparison.Ordinal));
    }
}
