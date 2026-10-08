using System.Text.Json;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Regras em vigor numa execução.</summary>
/// <param name="Options">Seção Sync efetiva.</param>
/// <param name="Active">Integração ativa (com a gestão desligada, sempre true).</param>
/// <param name="Version">Versão de solidesdp.configuracao usada; null com a gestão desligada.</param>
public sealed record SyncSettings(SyncOptions Options, bool Active, int? Version);

/// <summary>
/// Seção Sync da execução corrente (escopo da execução). Começa com o appsettings.json; o
/// <see cref="SyncPipeline"/> troca pela configuração do banco antes de chamar as etapas.
/// </summary>
public sealed class SyncOptionsAccessor(IOptions<SyncOptions> configured)
{
    private SyncOptions? _current;

    public SyncOptions Configured => configured.Value;

    public SyncOptions Current => _current ?? configured.Value;

    public void Use(SyncOptions options) => _current = options;
}

/// <summary>
/// Decide a configuração de cada execução. Gestão desligada: appsettings.json, como sempre.
/// Gestão ligada: versão vigente de solidesdp.configuracao (criando a versão 1 a partir do
/// appsettings.json na primeira vez), validada pelas mesmas regras do start.
/// </summary>
public sealed class SyncSettingsLoader(
    IOptions<SyncOptions> configured,
    IOptions<ManagementOptions> management,
    IOptions<SolidesDpOptions> solides,
    IManagementStore store,
    ILogger<SyncSettingsLoader> logger)
{
    public bool ManagementEnabled => management.Value.Habilitada;

    /// <exception cref="SyncAbortedException">Configuração do banco ilegível ou inválida.</exception>
    public async Task<SyncSettings> LoadAsync(CancellationToken ct)
    {
        var baseline = configured.Value;
        if (!ManagementEnabled)
        {
            return new SyncSettings(baseline, Active: true, Version: null);
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

        var validation = new SyncOptionsValidator().Validate(null, options);
        if (validation.Failed)
        {
            throw new SyncAbortedException($"config_invalid: versão {current.Version} de solidesdp.configuracao: {validation.FailureMessage}");
        }

        if (!options.DryRun && string.IsNullOrWhiteSpace(solides.Value.Token))
        {
            throw new SyncAbortedException($"config_invalid: a versão {current.Version} desliga o dry-run, mas SolidesDP:Token não está configurado.");
        }

        return new SyncSettings(options, current.Active, current.Version);
    }

    /// <summary>Com a gestão ligada, grava na execução quem pediu e a versão da configuração.</summary>
    public Task TagRunAsync(Guid runId, string? requestedBy, SyncSettings settings, CancellationToken ct) =>
        ManagementEnabled ? store.TagRunAsync(runId, requestedBy, settings.Version, ct) : Task.CompletedTask;
}
