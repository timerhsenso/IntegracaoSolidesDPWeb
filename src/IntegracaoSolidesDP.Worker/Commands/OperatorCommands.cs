using Dapper;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Commands;

/// <summary>
/// Comandos de operação (instalação, piloto, suporte). Escrevem no console. Cada empresa é uma conta do
/// Sólides DP: discover e reconcile são de uma empresa (--empresa N, ou a única habilitada).
/// </summary>
public sealed class OperatorCommands(
    ConnectionFactory connections,
    ISolidesDpClient api,
    SolidesDpAccount account,
    SyncSettingsLoader settingsLoader,
    IStateStore state,
    IOptions<SolidesDpOptions> solidesOptions,
    IOptions<SyncOptions> syncOptions,
    IOptions<ExecutionOptions> executionOptions,
    IOptions<ManagementOptions> managementOptions,
    IManagementStore management,
    TextWriter output)
{
    private static readonly string[] RequiredTables = ["func1", "cargo1", "temp1", "test1", "tcus1", "tsitu1", "feria2"];

    public Task<int> CheckConfigAsync(CancellationToken ct) => CheckConfigAsync(cdempresa: null, ct);

    /// <summary>Confere banco, configuração e token; com <paramref name="cdempresa"/>, só aquela empresa.</summary>
    public async Task<int> CheckConfigAsync(int? cdempresa, CancellationToken ct)
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

            if (managementOptions.Value.Habilitada)
            {
                await management.EnsureSchemaAsync(ct);
                var current = await management.GetCurrentConfigurationAsync(sync.InstanceName, ct);
                await output.WriteLineAsync(current is null
                    ? "[--] Gestão pela Web ligada: a configuração será criada a partir do appsettings.json na primeira execução"
                    : $"[OK] Gestão pela Web ligada: valem as regras da versão {current.Version} ({(current.Active ? "ativa" : "DESATIVADA")}), " +
                      $"gravada por {current.CreatedBy} em {current.CreatedAt:yyyy-MM-dd HH:mm}");
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await output.WriteLineAsync($"[ERRO] Banco: {ex.Message}");
            return 1;
        }

        SyncSettings settings;
        try
        {
            settings = await settingsLoader.LoadAsync(ct);
        }
        catch (SyncAbortedException ex)
        {
            await output.WriteLineAsync($"[ERRO] Configuração: {ex.Message}");
            return 1;
        }

        var empresas = settings.Empresas.Where(e => cdempresa is null || e.Cdempresa == cdempresa).ToList();
        if (empresas.Count == 0)
        {
            ok &= cdempresa is null;
            await output.WriteLineAsync(cdempresa is { } pedida
                ? FormattableString.Invariant($"[ERRO] A empresa {pedida} não está habilitada na integração (tela Empresas da Web)")
                : "[--] Nenhuma empresa habilitada: nada será sincronizado (habilite na tela Empresas da Web)");
        }

        foreach (var empresa in empresas)
        {
            var options = empresa.Options;
            var filiais = empresa.Filiais.Count == 0 ? "todas as filiais ativas" : $"filiais {string.Join(", ", empresa.Filiais.Order())}";
            await output.WriteLineAsync(FormattableString.Invariant(
                $"Empresa {empresa.Cdempresa}: {(options.DryRun ? "DRY-RUN (nada é enviado)" : "REAL")}; {filiais}; tipos de colaborador: {string.Join(",", options.TiposColaborador)}; go-live: {options.GoLiveDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}"));

            if (empresa.Problema is { } problema && !options.DryRun)
            {
                ok = false;
                await output.WriteLineAsync($"[ERRO] {problema}");
            }

            if (empresa.Token is null)
            {
                await output.WriteLineAsync(FormattableString.Invariant($"[--] Empresa {empresa.Cdempresa}: sem token do Sólides DP (ok para dry-run)"));
                continue;
            }

            account.Use(empresa.Cdempresa, empresa.Token);
            var test = await api.TestAsync(ct);
            ok &= test.IsSuccess;
            await output.WriteLineAsync(test.IsSuccess
                ? FormattableString.Invariant($"[OK] Empresa {empresa.Cdempresa}: token aceito pelo Sólides DP ({test.Value})")
                : FormattableString.Invariant($"[ERRO] Empresa {empresa.Cdempresa}: Sólides DP {test.Outcome} HTTP {test.HttpStatus} {test.Message}"));
        }

        account.Clear();
        return ok ? 0 : 1;
    }

    public async Task<int> DiscoverAsync(int? cdempresa, CancellationToken ct)
    {
        var empresa = await SingleEmpresaAsync(cdempresa, ct);
        if (empresa is null)
        {
            return 1;
        }

        if (empresa.Token is null)
        {
            await output.WriteLineAsync(FormattableString.Invariant($"A empresa {empresa.Cdempresa} não tem token do Sólides DP; cadastre o token para consultar."));
            return 1;
        }

        account.Use(empresa.Cdempresa, empresa.Token);
        var test = await api.TestAsync(ct);
        if (!test.IsSuccess)
        {
            await output.WriteLineAsync($"Token recusado: {test.Outcome} HTTP {test.HttpStatus} {test.Message}");
            return 1;
        }

        await output.WriteLineAsync(FormattableString.Invariant($"Conta do Sólides DP da empresa {empresa.Cdempresa}"));
        await Section("Empresas (modo PorCnpj casa pelo CNPJ)", await api.GetCompaniesAsync(ct),
            c => $"{c.Id,8}  {c.Cnpj,-18} {c.ExternalId,-12} {c.FantasyName ?? c.SocialReason}");
        await Section("Escalas (WorkScheduleExternalId)", await api.GetWorkSchedulesAsync(ct),
            s => $"{s.Id,8}  {s.ExternalId,-20} {(s.Standard == true ? "[padrão] " : string.Empty)}{s.Name}");
        await Section("Regras de ponto (PunchRuleExternalId)", await api.GetPunchRulesAsync(ct),
            r => $"{r.Id,8}  {r.ExternalId,-20} {(r.Standard == true ? "[padrão] " : string.Empty)}{r.Description}");
        await Section("Motivos de ajuste (FeriasMotivoId)", await api.GetAdjustmentReasonsAsync(ct),
            r => $"{r.Id,8}  {r.Description}{(r.Active == false ? " (inativo)" : string.Empty)}");
        return 0;
    }

    /// <summary>Confere no DP, pelo id, os colaboradores vinculados da empresa. Com repair, os ausentes voltam a ser procurados pelo CPF.</summary>
    public async Task<int> ReconcileAsync(int? cdempresa, bool repair, CancellationToken ct)
    {
        var empresa = await SingleEmpresaAsync(cdempresa, ct);
        if (empresa is null)
        {
            return 1;
        }

        if (empresa.Token is null)
        {
            await output.WriteLineAsync(FormattableString.Invariant($"A empresa {empresa.Cdempresa} não tem token do Sólides DP."));
            return 1;
        }

        account.Use(empresa.Cdempresa, empresa.Token);
        await state.EnsureSchemaAsync(ct);
        var employees = await state.LoadEntityStatesAsync(empresa.Cdempresa, EntityTypes.Employee, ct);
        var missing = 0;
        var unverifiable = 0;

        foreach (var employee in employees.Values.Where(e => e.RemoteId is not null && e.Status == EntityStatuses.Synced))
        {
            var label = EmployeeKey.Rotulo(empresa.Cdempresa, employee.Matricula ?? "?");
            var result = await api.FindEmployeeByIdAsync(employee.RemoteId!.Value, ct);
            switch (result.Outcome)
            {
                case ApiOutcome.Success:
                    continue;
                case ApiOutcome.NotFound:
                    missing++;
                    await output.WriteLineAsync($"[AUSENTE] {label} (id {employee.RemoteId})");
                    if (repair)
                    {
                        // Sem id: a próxima execução procura o CPF no DP e vincula, ou cria.
                        await state.UpsertEntityStateAsync(empresa.Cdempresa, employee with { PayloadHash = null, RemoteId = null }, ct);
                    }

                    break;
                default:
                    unverifiable++;
                    await output.WriteLineAsync($"[?] {label}: {result.Outcome} {result.Message}");
                    break;
            }
        }

        await output.WriteLineAsync(FormattableString.Invariant(
            $"Empresa {empresa.Cdempresa}: conferidos {employees.Count}; ausentes no DP: {missing}; não verificáveis: {unverifiable}{(repair && missing > 0 ? " (marcados para nova busca pelo CPF)" : string.Empty)}"));
        return missing == 0 && unverifiable == 0 ? 0 : 2;
    }

    /// <summary>A empresa pedida, ou a única habilitada. Escreve o motivo e devolve null quando não dá para escolher.</summary>
    private async Task<EmpresaSettings?> SingleEmpresaAsync(int? cdempresa, CancellationToken ct)
    {
        SyncSettings settings;
        try
        {
            settings = await settingsLoader.LoadAsync(ct);
        }
        catch (SyncAbortedException ex)
        {
            await output.WriteLineAsync($"Configuração inválida: {ex.Message}");
            return null;
        }

        if (cdempresa is { } pedida)
        {
            var found = settings.Empresas.FirstOrDefault(e => e.Cdempresa == pedida);
            if (found is null)
            {
                await output.WriteLineAsync(FormattableString.Invariant($"A empresa {pedida} não está habilitada na integração."));
            }

            return found;
        }

        if (settings.Empresas.Count == 1)
        {
            return settings.Empresas[0];
        }

        await output.WriteLineAsync(settings.Empresas.Count == 0
            ? "Nenhuma empresa habilitada na integração."
            : $"Cada empresa é uma conta do Sólides DP: informe --empresa N ({string.Join(", ", settings.Empresas.Select(e => e.Cdempresa))}).");
        return null;
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
