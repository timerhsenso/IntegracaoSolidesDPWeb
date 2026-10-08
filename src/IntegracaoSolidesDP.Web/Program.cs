using IntegracaoSolidesDP.Web.Infrastructure;
using Serilog;
using Serilog.Settings.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions(
        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly))
    .Enrich.FromLogContext());

builder.Services.AddIntegracaoSolidesDPWeb(builder.Configuration);

var app = builder.Build();
await app.PrepararBancoAsync();
app.UseIntegracaoSolidesDPWeb();
await app.RunAsync();
