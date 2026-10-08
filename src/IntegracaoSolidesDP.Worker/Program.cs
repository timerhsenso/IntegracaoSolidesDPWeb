using IntegracaoSolidesDP.Worker.Commands;
using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.Scheduling;
using System.Reflection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Settings.Configuration;

// Como serviço Windows o diretório corrente é System32: caminhos relativos (logs/, reports/)
// passam a ser relativos ao executável, em qualquer forma de execução.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var command = CliCommand.Parse(args);
if (command.Mode == CliMode.Version)
{
    Console.WriteLine(ProductVersion());
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = command.HostArgs,
    // Como Windows Service o diretório corrente é System32: appsettings e logs ficam junto do executável.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = "IntegracaoSolidesDP");
builder.Services.AddSystemd();
// Assemblies dos sinks passados explicitamente: no executável single-file o Serilog não
// consegue descobri-los pelo "Using" do appsettings.
builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions(
        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly))
    .Enrich.FromLogContext());

builder.Services.AddIntegracaoSolidesDP(builder.Configuration);
if (command.Mode == CliMode.Service)
{
    builder.Services.AddHostedService<SyncWorker>();
}

IHost host;
try
{
    host = builder.Build();
    // Valida as options agora (ValidateOnStart só roda no StartAsync, que os comandos não chamam).
    _ = host.Services.GetRequiredService<IOptions<IntegracaoSolidesDP.Worker.Options.SolidesDpOptions>>().Value;
    _ = host.Services.GetRequiredService<IOptions<IntegracaoSolidesDP.Worker.Options.ExecutionOptions>>().Value;
    _ = host.Services.GetRequiredService<IOptions<IntegracaoSolidesDP.Worker.Options.SyncOptions>>().Value;
}
catch (OptionsValidationException ex)
{
    await Console.Error.WriteLineAsync("Configuração inválida:");
    foreach (var failure in ex.Failures)
    {
        await Console.Error.WriteLineAsync($"  - {failure}");
    }

    return 2;
}

if (command.Mode == CliMode.Service)
{
    await host.RunAsync();
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

await using var scope = host.Services.CreateAsyncScope();
var services = scope.ServiceProvider;
var ct = cancellation.Token;

switch (command.Mode)
{
    case CliMode.CheckConfig:
        return await services.GetRequiredService<OperatorCommands>().CheckConfigAsync(ct);
    case CliMode.Discover:
        return await services.GetRequiredService<OperatorCommands>().DiscoverAsync(ct);
    case CliMode.Reconcile:
        return await services.GetRequiredService<OperatorCommands>().ReconcileAsync(command.Repair, ct);
    default:
        var summary = await services.GetRequiredService<SyncPipeline>().RunAsync(
            command.Mode == CliMode.DryRun ? "cli-dry-run" : "cli", command.Mode == CliMode.DryRun ? true : null, ct);
        Console.WriteLine($"{summary.Status} — relatório: {summary.ReportPath}");
        return summary.Status is "completed" ? 0 : 1;
}

static string ProductVersion() =>
    typeof(CliCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
    ?? "desconhecida";
