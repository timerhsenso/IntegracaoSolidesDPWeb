using Dapper;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Commands;

/// <summary>Comandos de operação (instalação, piloto, suporte). Escrevem no console.</summary>
public sealed class OperatorCommands(
    ConnectionFactory connections,
    ISolidesDpClient api,
    IStateStore state,
    IOptions<SolidesDpOptions> solidesOptions,
    IOptions<SyncOptions> syncOptions,
    IOptions<ExecutionOptions> executionOptions,
    TextWriter output)
{
    private static readonly string[] RequiredTables = ["func1", "cargo1", "test1", "tcus1", "tsitu1", "feria2"];

    public async Task<int> CheckConfigAsync(CancellationToken ct)
    {
        // As options já foram validadas no start (ValidateOnStart); aqui confere os recursos externos.
        var ok = true;
        var sync = syncOptions.Value;
        var execution = executionOptions.Value;
        var version = typeof(OperatorCommands).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0];
        await output.WriteLineAsync($"IntegracaoSolidesDP {version}");
        await output.WriteLineAsync($"Execução: {(execution.Interval is { } i ? $"a cada {i}" : $"às {string.Join(", ", execution.TimesOfDay ?? [])}")} ({execution.TimeZone})");
        await output.WriteLineAsync($"Modo: {(sync.DryRun ? "DRY-RUN (nada é enviado)" : "REAL")}; tipos de colaborador: {string.Join(",", sync.TiposColaborador)}; go-live: {sync.GoLiveDate?.ToString("yyyy-MM-dd") ?? "-"}");
        await output.WriteLineAsync($"Sólides DP: {solidesOptions.Value.BaseUrl}");

        try
        {
            await using var connection = await connections.OpenAsync(ct);
            var found = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND name IN @Names",
                new { Names = RequiredTables }, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = RequiredTables.Where(t => !found.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                ok = false;
                await output.WriteLineAsync($"[ERRO] Banco: tabelas ausentes em dbo: {string.Join(", ", missing)}");
            }
            else
            {
                await output.WriteLineAsync($"[OK] Banco: {connection.Database} em {connection.DataSource}");
            }

            await state.EnsureSchemaAsync(ct);
            await output.WriteLineAsync("[OK] Schema solidesdp criado/atualizado");
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            ok = false;
            await output.WriteLineAsync($"[ERRO] Banco: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(solidesOptions.Value.Token))
        {
            await output.WriteLineAsync("[--] Sólides DP: sem token configurado (ok para dry-run)");
        }
        else
        {
            var test = await api.TestAsync(ct);
            ok &= test.IsSuccess;
            await output.WriteLineAsync(test.IsSuccess
                ? $"[OK] Sólides DP: token aceito ({test.Value})"
                : $"[ERRO] Sólides DP: {test.Outcome} HTTP {test.HttpStatus} {test.Message}");
        }

        return ok ? 0 : 1;
    }

    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(solidesOptions.Value.Token))
        {
            await output.WriteLineAsync("Configure SolidesDP:Token para consultar o Sólides DP.");
            return 1;
        }

        var test = await api.TestAsync(ct);
        if (!test.IsSuccess)
        {
            await output.WriteLineAsync($"Token recusado: {test.Outcome} HTTP {test.HttpStatus} {test.Message}");
            return 1;
        }

        await Section("Empresas (Sync:CompanyMode=ResolveByCnpj casa pelo CNPJ)", await api.GetCompaniesAsync(ct),
            c => $"{c.Id,8}  {c.Cnpj,-18} {c.ExternalId,-12} {c.FantasyName ?? c.SocialReason}");
        await Section("Escalas (Sync:WorkScheduleExternalId)", await api.GetWorkSchedulesAsync(ct),
            s => $"{s.Id,8}  {s.ExternalId,-20} {(s.Standard == true ? "[padrão] " : string.Empty)}{s.Name}");
        await Section("Regras de ponto (Sync:PunchRuleExternalId)", await api.GetPunchRulesAsync(ct),
            r => $"{r.Id,8}  {r.ExternalId,-20} {(r.Standard == true ? "[padrão] " : string.Empty)}{r.Description}");
        await Section("Motivos de ajuste (Sync:FeriasMotivoId)", await api.GetAdjustmentReasonsAsync(ct),
            r => $"{r.Id,8}  {r.Description}{(r.Active == false ? " (inativo)" : string.Empty)}");
        return 0;
    }

    public async Task<int> ReconcileAsync(bool repair, CancellationToken ct)
    {
        await state.EnsureSchemaAsync(ct);
        var employees = await state.LoadEntityStatesAsync(EntityTypes.Employee, ct);
        var missing = 0;
        var unverifiable = 0;

        foreach (var employee in employees.Values.Where(e => e.RemoteId is not null && e.Status == EntityStatuses.Synced))
        {
            var result = await api.FindEmployeeAsync(employee.ExternalId, ct);
            switch (result.Outcome)
            {
                case ApiOutcome.Success:
                    continue;
                case ApiOutcome.NotFound:
                    missing++;
                    await output.WriteLineAsync($"[AUSENTE] {employee.ExternalId} (id {employee.RemoteId})");
                    if (repair)
                    {
                        // Limpa o hash local: a próxima execução reenvia (e o register recria pelo externalId).
                        await state.UpsertEntityStateAsync(employee with { PayloadHash = null, RemoteId = null }, ct);
                    }

                    break;
                default:
                    unverifiable++;
                    await output.WriteLineAsync($"[?] {employee.ExternalId}: {result.Outcome} {result.Message}");
                    break;
            }
        }

        await output.WriteLineAsync($"Conferidos {employees.Count}; ausentes no DP: {missing}; não verificáveis: {unverifiable}{(repair && missing > 0 ? " (marcados para reenvio)" : string.Empty)}");
        return missing == 0 && unverifiable == 0 ? 0 : 2;
    }

    private async Task Section<T>(string title, ApiResult<IReadOnlyList<T>> result, Func<T, string> line)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync($"== {title}");
        if (!result.IsSuccess)
        {
            await output.WriteLineAsync($"   falhou: {result.Outcome} HTTP {result.HttpStatus} {result.Message}");
            return;
        }

        foreach (var item in result.Value!)
        {
            await output.WriteLineAsync("   " + line(item));
        }
    }
}
