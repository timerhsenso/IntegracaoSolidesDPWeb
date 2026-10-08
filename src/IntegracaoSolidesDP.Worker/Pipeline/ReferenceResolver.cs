using System.Globalization;
using System.Text;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>
/// Antes de escrever qualquer coisa: valida o token e resolve no DP o que o worker só
/// referencia (escala, regra de ponto, empresas por CNPJ, motivo de ajuste FÉRIAS).
/// </summary>
public sealed class ReferenceResolver(
    ISolidesDpClient api,
    ISourceReader source,
    IOptions<SyncOptions> syncOptions,
    ILogger<ReferenceResolver> logger)
{
    private SyncOptions Options => syncOptions.Value;

    public async Task ResolveAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        Ensure(await api.TestAsync(ct), "token");

        await ResolveWorkScheduleAsync(context, ct);
        await ResolvePunchRuleAsync(context, ct);
        if (Options.CompanyMode == CompanyMode.ResolveByCnpj)
        {
            await ResolveCompaniesAsync(context, plan, ct);
        }

        await ResolveVacationReasonAsync(context, ct);
    }

    private async Task ResolveWorkScheduleAsync(SyncContext context, CancellationToken ct)
    {
        var schedules = Ensure(await api.GetWorkSchedulesAsync(ct), "escalas");
        var chosen = string.IsNullOrWhiteSpace(Options.WorkScheduleExternalId)
            ? schedules.FirstOrDefault(s => s.Standard == true)
            : schedules.FirstOrDefault(s => string.Equals(s.ExternalId, Options.WorkScheduleExternalId, StringComparison.Ordinal));

        context.DefaultWorkScheduleId = chosen?.Id ?? throw new SyncAbortedException(
            string.IsNullOrWhiteSpace(Options.WorkScheduleExternalId)
                ? "Nenhuma escala padrão no Sólides DP. Defina Sync:WorkScheduleExternalId (rode --discover)."
                : $"Escala '{Options.WorkScheduleExternalId}' não existe no Sólides DP (rode --discover).");
        logger.LogInformation("Escala para novos colaboradores: {Id} {Name}", chosen.Id, chosen.Name);
    }

    private async Task ResolvePunchRuleAsync(SyncContext context, CancellationToken ct)
    {
        var rules = Ensure(await api.GetPunchRulesAsync(ct), "regras de ponto");
        if (!string.IsNullOrWhiteSpace(Options.PunchRuleExternalId))
        {
            if (!rules.Any(r => string.Equals(r.ExternalId, Options.PunchRuleExternalId, StringComparison.Ordinal)))
            {
                throw new SyncAbortedException($"Regra de ponto '{Options.PunchRuleExternalId}' não existe no Sólides DP (rode --discover).");
            }

            context.DefaultPunchRuleExternalId = Options.PunchRuleExternalId;
            return;
        }

        // O EmployeeDTO só referencia regra por externalId. Sem externalId na padrão, o DP aplica a dele.
        context.DefaultPunchRuleExternalId = rules.FirstOrDefault(r => r.Standard == true)?.ExternalId;
    }

    private async Task ResolveCompaniesAsync(SyncContext context, EmployeePlan plan, CancellationToken ct)
    {
        foreach (var company in Ensure(await api.GetCompaniesAsync(ct), "empresas"))
        {
            if (EmployeeMapper.Digits(company.Cnpj) is { Length: 14 } cnpj)
            {
                context.CompanyIdsByCnpj.TryAdd(cnpj, company.Id);
            }
        }

        var missing = plan.Active
            .Select(r => EmployeeMapper.Digits(r.Cnpj))
            .OfType<string>()
            .Where(c => !context.CompanyIdsByCnpj.ContainsKey(c))
            .ToHashSet(StringComparer.Ordinal);

        if (missing.Count == 0)
        {
            return;
        }

        if (!Options.CreateMissingCompanies)
        {
            foreach (var cnpj in missing)
            {
                context.Add(new RunItem(EntityTypes.Company, cnpj, ItemActions.Resolve, ItemStatuses.Failed,
                    Message: "company_not_found: CNPJ não cadastrado no Sólides DP; colaboradores desta empresa ficam bloqueados"));
            }

            return;
        }

        var names = (await source.ReadWorkplacesAsync(ct))
            .Where(w => EmployeeMapper.Digits(w.Cnpj) is { } c && missing.Contains(c))
            .GroupBy(w => EmployeeMapper.Digits(w.Cnpj)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(w => w.Cdfilial).First(), StringComparer.Ordinal);

        foreach (var cnpj in missing)
        {
            names.TryGetValue(cnpj, out var workplace);
            var result = await api.CreateCompanyAsync(new CompanyRequest(cnpj, workplace?.NomeFantasia, workplace?.Descricao), ct);
            ThrowIfUnauthorized(result);
            if (result is { IsSuccess: true, Value.Id: > 0 })
            {
                context.CompanyIdsByCnpj[cnpj] = result.Value.Id;
                context.Add(new RunItem(EntityTypes.Company, cnpj, ItemActions.Create, ItemStatuses.Created, result.HttpStatus));
            }
            else
            {
                context.Add(new RunItem(EntityTypes.Company, cnpj, ItemActions.Create, ItemStatuses.Failed, result.HttpStatus, result.Message));
            }
        }
    }

    private async Task ResolveVacationReasonAsync(SyncContext context, CancellationToken ct)
    {
        if (Options.FeriasMotivoId is { } configured)
        {
            context.FeriasReasonId = configured;
            return;
        }

        var reasons = Ensure(await api.GetAdjustmentReasonsAsync(ct), "motivos de ajuste");
        var ferias = reasons.Where(r => Normalize(r.Description) == "FERIAS" && r.Active != false).ToList();
        if (ferias.Count == 1)
        {
            context.FeriasReasonId = ferias[0].Id;
            return;
        }

        context.Add(new RunItem(EntityTypes.Vacation, "-", ItemActions.Resolve, ItemStatuses.Failed,
            Message: ferias.Count == 0
                ? "Motivo de ajuste FÉRIAS não encontrado; defina Sync:FeriasMotivoId (rode --discover)"
                : $"Mais de um motivo FÉRIAS ({string.Join(", ", ferias.Select(f => f.Id))}); defina Sync:FeriasMotivoId"));
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToUpperInvariant();
    }

    private static T Ensure<T>(ApiResult<T> result, string what)
    {
        ThrowIfUnauthorized(result);
        return result is { IsSuccess: true, Value: { } value }
            ? value
            : throw new SyncAbortedException($"Falha ao consultar {what} no Sólides DP: {result.Outcome} {result.Message}");
    }

    public static void ThrowIfUnauthorized<T>(ApiResult<T> result)
    {
        if (result.Outcome == ApiOutcome.Unauthorized)
        {
            throw new SyncAbortedException($"Sólides DP recusou o token (HTTP {result.HttpStatus}): {result.Message}");
        }
    }
}
