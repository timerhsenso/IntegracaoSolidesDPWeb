using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Erros;
using Serilog;
using Serilog.Settings.Configuration;

// Comandos de linha de comando (usados pelo deploy/windows/install-web.ps1):
//   --version       mostra a versão e sai
//   --check-config  confere configuração e permissões no banco sem iniciar o site (código de saída 0 = ok)
if (args.Contains("--version"))
{
    Console.WriteLine(VerificacaoConfiguracao.Versao());
    return 0;
}

var checkConfig = args.Contains("--check-config");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Na linha de comando, lê o appsettings.json da pasta do executável, de onde quer que seja chamado.
    // No IIS (e no dotnet run) vale o padrão: a pasta do site / do projeto.
    ContentRootPath = checkConfig ? AppContext.BaseDirectory : null,
});

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions(
        typeof(ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly))
    .Enrich.FromLogContext()
    // Erros (nível Error) também vão para solidesdp.erro_aplicacao: tela "Erros da aplicação".
    .WriteTo.Sink(new CapturaErrosSink(services.GetRequiredService<FilaErros>(), services.GetRequiredService<IHttpContextAccessor>())));

builder.Services.AddIntegracaoSolidesDPWeb(builder.Configuration);

var app = builder.Build();

if (checkConfig)
{
    return await VerificacaoConfiguracao.ExecutarAsync(app.Services, Console.Out, CancellationToken.None) ? 0 : 1;
}

await app.PrepararBancoAsync();
app.UseIntegracaoSolidesDPWeb();
await app.RunAsync();
return 0;
