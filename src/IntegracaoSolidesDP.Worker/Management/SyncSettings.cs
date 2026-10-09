using System.Security.Cryptography;
using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Regras em vigor numa execução.</summary>
/// <param name="Options">Seção Sync geral (a de cada empresa está em <see cref="EmpresaSettings.Options"/>).</param>
/// <param name="Active">Integração ativa (com a gestão desligada, sempre true).</param>
/// <param name="Version">Versão de solidesdp.configuracao usada; null com a gestão desligada.</param>
/// <param name="Empresas">Empresas habilitadas, cada uma uma conta do Sólides DP.</param>
public sealed record SyncSettings(SyncOptions Options, bool Active, int? Version, IReadOnlyList<EmpresaSettings> Empresas);

/// <summary>Uma empresa habilitada: regras efetivas, filiais marcadas e o token da conta dela no Sólides DP.</summary>
/// <param name="Cdempresa">dbo.temp1.cdempresa.</param>
/// <param name="Options">Seção Sync com as regras próprias da empresa aplicadas por cima das gerais.</param>
/// <param name="Filiais">Filiais marcadas; vazio = todas as filiais ativas.</param>
/// <param name="Token">Token decifrado; null se a empresa ainda não tem token.</param>
/// <param name="Problema">Configuração da empresa que impede a execução real (token ausente ou ilegível, regra inválida).</param>
public sealed record EmpresaSettings(int Cdempresa, SyncOptions Options, IReadOnlySet<int> Filiais, string? Token, string? Problema = null);

/// <summary>
/// Seção Sync da execução corrente (escopo da execução). Começa com o appsettings.json; o
/// <see cref="SyncPipeline"/> troca pela regra da empresa antes de chamar as etapas.
/// </summary>
public sealed class SyncOptionsAccessor(IOptions<SyncOptions> configured)
{
    private SyncOptions? _current;

    public SyncOptions Configured => configured.Value;

    public SyncOptions Current => _current ?? configured.Value;

    public void Use(SyncOptions options) => _current = options;
}

/// <summary>
/// Decide a configuração de cada execução.
/// <list type="bullet">
/// <item>Gestão desligada: appsettings.json. Uma só empresa (Sync:EmpresasIncluidas), com o SolidesDP:Token.</item>
/// <item>Gestão ligada: regras gerais da versão vigente de solidesdp.configuracao (criando a versão 1 a partir do
/// appsettings.json na primeira vez) e as empresas habilitadas na tela Empresas, cada uma com o seu token cifrado e as
/// suas regras (simulação, go-live, piloto, filiais, conta). Sem empresa habilitada, nada é sincronizado.</item>
/// </list>
/// </summary>
public sealed class SyncSettingsLoader(
    IOptions<SyncOptions> configured,
    IOptions<ManagementOptions> management,
    IOptions<SolidesDpOptions> solides,
    IManagementStore store,
    ITokenProtector tokens,
    ILogger<SyncSettingsLoader> logger)
{
    public bool ManagementEnabled => management.Value.Habilitada;

    /// <exception cref="SyncAbortedException">Configuração do banco ilegível ou inválida.</exception>
    public async Task<SyncSettings> LoadAsync(CancellationToken ct)
    {
        var baseline = configured.Value;
        if (!ManagementEnabled)
        {
            return new SyncSettings(baseline, Active: true, Version: null, [SingleAccount(baseline)]);
        }

        await store.EnsureSchemaAsync(ct);
        var current = await store.GetCurrentConfigurationAsync(baseline.InstanceName, ct);
        if (current is null)
        {
            await store.SeedConfigurationAsync(baseline.InstanceName, SyncOptionsJson.Serialize(baseline), ct);
            current = await store.GetCurrentConfigurationAsync(baseline.InstanceName, ct)
                      ?? throw new SyncAbortedException("config_missing: não foi possível criar a versão 1 de solidesdp.configuracao.");
            logger.LogInformation("Configuração da instância {Instance} criada no banco a partir do appsettings.json (versão 1)", baseline.InstanceName);
        }

        SyncOptions options;
        try
        {
            options = SyncOptionsJson.Deserialize(current.SyncJson);
        }
        catch (JsonException ex)
        {
            throw new SyncAbortedException($"config_invalid: versão {current.Version} de solidesdp.configuracao não é um JSON válido da seção Sync: {ex.Message}");
        }

        // Identidade da instalação: continua sendo do appsettings.json, nunca do banco.
        options.InstanceName = baseline.InstanceName;
        options.ReportDirectory = baseline.ReportDirectory;

        // As exigências da execução real (go-live, token) são conferidas por empresa.
        var validation = new SyncOptionsValidator().Validate(null, EmpresaOptions.Copiar(options, dryRun: true));
        if (validation.Failed)
        {
            throw new SyncAbortedException($"config_invalid: versão {current.Version} de solidesdp.configuracao: {validation.FailureMessage}");
        }

        var stored = await store.GetEmpresaTokensAsync(ct);
        var empresas = current.Empresas
            .Where(e => e.Habilitada)
            .OrderBy(e => e.Cdempresa)
            .Select(e => ForEmpresa(options, e, stored.GetValueOrDefault(e.Cdempresa)))
            .ToList();
        return new SyncSettings(options, current.Active, current.Version, empresas);
    }

    /// <summary>Com a gestão ligada, grava na execução quem pediu e a versão da configuração.</summary>
    public Task TagRunAsync(Guid runId, string? requestedBy, SyncSettings settings, CancellationToken ct) =>
        ManagementEnabled ? store.TagRunAsync(runId, requestedBy, settings.Version, ct) : Task.CompletedTask;

    /// <summary>Regra da empresa por cima da geral (ver <see cref="EmpresaOptions.Mesclar"/>).</summary>
    public static SyncOptions OptionsFor(SyncOptions geral, EmpresaConfiguracao empresa) => EmpresaOptions.Mesclar(geral, empresa);

    private EmpresaSettings ForEmpresa(SyncOptions geral, EmpresaConfiguracao empresa, EmpresaToken? stored)
    {
        var options = OptionsFor(geral, empresa);
        var problemas = new List<string>();

        string? token = null;
        if (stored?.TokenCifrado is { } cifrado)
        {
            try
            {
                token = SolidesDpToken.Normalize(tokens.Unprotect(cifrado));
            }
            catch (CryptographicException ex)
            {
                problemas.Add($"o token não pôde ser decifrado nesta máquina ({ex.Message}); cadastre o token de novo na tela Empresas");
            }
        }

        if (!options.DryRun)
        {
            if (options.GoLiveDate is null)
            {
                problemas.Add("a empresa não tem data de go-live (obrigatória para o envio real); informe na tela Empresas");
            }

            var validation = new SyncOptionsValidator().Validate(null, EmpresaOptions.Copiar(options, dryRun: true));
            if (validation.Failed)
            {
                problemas.Add(validation.FailureMessage);
            }

            if (token is null && stored?.TokenCifrado is null)
            {
                problemas.Add("a empresa não tem token do Sólides DP; cadastre na tela Empresas");
            }
        }

        return new EmpresaSettings(empresa.Cdempresa, options, empresa.Filiais.ToHashSet(), token, Problema(empresa.Cdempresa, problemas));
    }

    /// <summary>Uma conta só: a empresa de Sync:EmpresasIncluidas, com o SolidesDP:Token.</summary>
    private EmpresaSettings SingleAccount(SyncOptions options)
    {
        var empresas = options.EmpresasIncluidas.Distinct().ToList();
        if (empresas.Count != 1)
        {
            throw new SyncAbortedException(
                "config_invalid: cada empresa é uma conta do Sólides DP, com o seu token. Sem empresas configuradas na tela Empresas, " +
                $"Sync:EmpresasIncluidas precisa ter exatamente uma empresa (tem {empresas.Count}).");
        }

        var token = SolidesDpToken.Normalize(solides.Value.Token);
        var problemas = new List<string>();
        if (!options.DryRun)
        {
            var validation = new SyncOptionsValidator().Validate(null, options);
            if (validation.Failed)
            {
                problemas.Add(validation.FailureMessage);
            }

            if (token is null)
            {
                problemas.Add("fora do dry-run, SolidesDP:Token é obrigatório");
            }
        }

        return new EmpresaSettings(empresas[0], options, new HashSet<int>(), token, Problema(empresas[0], problemas));
    }

    private static string? Problema(int cdempresa, List<string> problemas) =>
        problemas.Count == 0 ? null : FormattableString.Invariant($"config_invalid: empresa {cdempresa}: {string.Join("; ", problemas)}");
}
