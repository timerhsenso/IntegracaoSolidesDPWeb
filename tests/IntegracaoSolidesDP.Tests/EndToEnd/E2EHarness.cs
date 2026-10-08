using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SolidesDP.Fake;
using SolidesDP.Fake.Testing;

namespace IntegracaoSolidesDP.Tests.EndToEnd;

/// <summary>
/// Worker de verdade (mesmo registro de DI do Program) contra o SQL Server do Testcontainers
/// e o fake do Sólides DP rodando in-process. Nenhuma rede envolvida além do Docker.
/// </summary>
public sealed class E2EHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<FakeApi> _fake = new();
    private readonly string _reports = Directory.CreateTempSubdirectory("solidesdp-reports").FullName;
    private readonly SqlServerFixture _db;

    public E2EHarness(SqlServerFixture db)
    {
        _db = db;
        Fake = new FakeAdminClient(_fake.CreateClient());
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(-3)));
    }

    public FakeAdminClient Fake { get; }

    /// <summary>Cliente autenticado da API fake, para simular alterações feitas pelo RH direto no DP.</summary>
    public HttpClient DpAsHr()
    {
        var client = _fake.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "fake-token");
        return client;
    }

    public FakeTimeProvider Clock { get; }

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["SolidesDP:BaseUrl"] = "http://solidesdp-fake",
        ["SolidesDP:Token"] = "fake-token",
        ["SolidesDP:TimeoutSeconds"] = "5",
        ["Execution:Interval"] = "00:30:00",
        ["Execution:TimeZone"] = "America/Bahia",
        ["Sync:DryRun"] = "false",
        ["Sync:GoLiveDate"] = "2026-01-01",
        ["Sync:FeriasJanelaDias"] = "60",
    };

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        await using var provider = BuildWorker();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SyncPipeline>().RunAsync("e2e", dryRunOverride: null, ct);
    }

    /// <summary>Requests de escrita (POST/PUT/DELETE) recebidos pelo fake desde a última limpeza.</summary>
    public async Task<IReadOnlyList<SolidesDP.Fake.Domain.RecordedRequest>> WritesAsync(CancellationToken ct = default) =>
        (await Fake.GetRequestsAsync(ct)).Where(r => r.Method is "POST" or "PUT" or "DELETE").ToList();

    private ServiceProvider BuildWorker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(Settings)
            {
                ["ConnectionStrings:Rhu"] = _db.ConnectionString,
                ["Sync:ReportDirectory"] = _reports,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Development", ApplicationName = "e2e" });
        services.AddIntegracaoSolidesDP(configuration);
        services.AddSingleton<TimeProvider>(Clock);

        foreach (var name in new[] { SolidesDpClient.RetryClientName, SolidesDpClient.NoRetryClientName })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => _fake.Server.CreateHandler());
        }

        services.PostConfigure<HttpStandardResilienceOptions>(SolidesDpClient.RetryClientName, o =>
        {
            o.Retry.Delay = TimeSpan.FromMilliseconds(1);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(20);
            o.Retry.UseJitter = false;
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        await _fake.DisposeAsync();
        try
        {
            Directory.Delete(_reports, recursive: true);
        }
        catch (IOException)
        {
            // Relatórios temporários; não falhar o teste por isso.
        }
    }
}
