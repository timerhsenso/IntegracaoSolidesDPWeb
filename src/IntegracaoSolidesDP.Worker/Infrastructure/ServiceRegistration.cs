using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Commands;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.Pipeline.Steps;
using IntegracaoSolidesDP.Worker.Scheduling;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddIntegracaoSolidesDP(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SolidesDpOptions>().Bind(configuration.GetSection(SolidesDpOptions.SectionName)).ValidateOnStart();
        services.AddOptions<ExecutionOptions>().Bind(configuration.GetSection(ExecutionOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SyncOptions>()
            .Bind(configuration.GetSection(SyncOptions.SectionName))
            .PostConfigure(options => ReplaceConfiguredLists(options, configuration.GetSection(SyncOptions.SectionName)))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SolidesDpOptions>, SolidesDpOptionsValidator>();
        services.AddSingleton<IValidateOptions<ExecutionOptions>, ExecutionOptionsValidator>();
        services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString("Rhu");
            return string.IsNullOrWhiteSpace(connectionString)
                ? throw new OptionsValidationException("ConnectionStrings:Rhu", typeof(string), ["ConnectionStrings:Rhu é obrigatório."])
                : new ConnectionFactory(connectionString);
        });
        services.AddSingleton(sp =>
        {
            var zoneId = sp.GetRequiredService<IOptions<ExecutionOptions>>().Value.TimeZone;
            return ExecutionOptionsValidator.TryFindTimeZone(zoneId, out var zone)
                ? new EpochDates(zone)
                : throw new OptionsValidationException("Execution:TimeZone", typeof(string), [$"Fuso '{zoneId}' desconhecido."]);
        });
        services.AddSingleton(sp => new ExecutionSchedule(
            sp.GetRequiredService<IOptions<ExecutionOptions>>().Value,
            sp.GetRequiredService<EpochDates>().Zone));

        services.AddSingleton<ISourceReader, SqlSourceReader>();
        services.AddSingleton<IStateStore, SqlStateStore>();
        services.AddSingleton<EmployeeMapper>();
        services.AddSingleton(sp => new VacationMapper(
            sp.GetRequiredService<EpochDates>(), sp.GetRequiredService<IOptions<SyncOptions>>().Value));
        services.AddSolidesDpClient();

        services.AddScoped<ReferenceResolver>();
        services.AddScoped<JobRoleStep>();
        services.AddScoped<WorkplaceStep>();
        services.AddScoped<DismissalStep>();
        services.AddScoped<EmployeeStep>();
        services.AddScoped<VacationStep>();
        services.AddScoped<SyncPipeline>();
        services.AddSingleton<RunReportWriter>();

        services.AddSingleton<TextWriter>(Console.Out);
        services.AddTransient<OperatorCommands>();
        return services;
    }

    /// <summary>
    /// O ConfigurationBinder ACRESCENTA itens a listas já inicializadas: com o padrão [1, 2] e
    /// "TiposColaborador": [1] no appsettings o resultado seria [1, 2, 1]. Lista configurada
    /// substitui o padrão.
    /// </summary>
    internal static void ReplaceConfiguredLists(SyncOptions options, IConfiguration section)
    {
        if (section.GetSection(nameof(SyncOptions.TiposColaborador)).Exists())
        {
            options.TiposColaborador = section.GetSection(nameof(SyncOptions.TiposColaborador)).Get<List<int>>() ?? [];
        }

        if (section.GetSection(nameof(SyncOptions.SituacoesIgnoradas)).Exists())
        {
            options.SituacoesIgnoradas = section.GetSection(nameof(SyncOptions.SituacoesIgnoradas)).Get<List<string>>() ?? [];
        }
    }
}
