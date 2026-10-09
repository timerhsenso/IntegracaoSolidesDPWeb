using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;
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
/// O fake isola as contas por token, como no Sólides DP real (uma conta por empresa): <see cref="Token"/> é a
/// conta padrão (empresa 1, gestão desligada) e <see cref="Token2"/> a de uma segunda empresa.
/// </summary>
public sealed class E2EHarness : IAsyncDisposable
{
    public const string Token = "fake-token";
    public const string Token2 = "fake-token-2";

    /// <summary>Data de go-live usada nas empresas dos testes (obrigatória para o envio real).</summary>
    public static readonly DateOnly GoLive = new(2026, 1, 1);

    /// <summary>Chave dos tokens cifrados (Gestao:ChaveTokens) usada nos testes.</summary>
    public static readonly string ChaveTokens = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly WebApplicationFactory<FakeApi> _root = new();
    private readonly WebApplicationFactory<FakeApi> _fake;
    private readonly string _reports = Directory.CreateTempSubdirectory("solidesdp-reports").FullName;
    private readonly SqlServerFixture _db;

    public E2EHarness(SqlServerFixture db)
    {
        _db = db;
        _fake = _root.WithWebHostBuilder(b => b
            .UseSetting("Fake:Tokens:0", Token)
            .UseSetting("Fake:Tokens:1", Token2)
            .UseSetting("Fake:IsolarContasPorToken", "true"));
        Fake = new FakeAdminClient(_fake.CreateClient());
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(-3)));
    }

    public FakeAdminClient Fake { get; }

    /// <summary>Cliente autenticado da API fake, para simular alterações feitas pelo RH direto no DP.</summary>
    public HttpClient DpAsHr(string token = Token)
    {
        var client = _fake.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        return client;
    }

    public FakeTimeProvider Clock { get; }

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["SolidesDP:BaseUrl"] = "http://solidesdp-fake",
        ["SolidesDP:Token"] = Token,
        ["SolidesDP:TimeoutSeconds"] = "5",
        ["Execution:Interval"] = "00:30:00",
        ["Execution:TimeZone"] = "America/Bahia",
        ["Sync:DryRun"] = "false",
        ["Sync:GoLiveDate"] = "2026-01-01",
        ["Sync:EmpresasIncluidas:0"] = "1",
        ["Sync:FeriasJanelaDias"] = "60",
    };

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        await using var provider = BuildWorker();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SyncPipeline>().RunAsync("e2e", dryRunOverride: null, ct);
    }

    /// <summary>
    /// Liga a gestão e grava, como a tela Empresas da Web faria, uma versão da configuração com estas empresas
    /// e os tokens cifrados de cada uma.
    /// </summary>
    public async Task ConfigurarEmpresasAsync(
        IReadOnlyList<EmpresaConfiguracao> empresas, IReadOnlyDictionary<int, string> tokens, CancellationToken ct = default)
    {
        Settings["Gestao:Habilitada"] = "true";
        Settings["Gestao:ChaveTokens"] = ChaveTokens;
        var store = new SqlManagementStore(_db.Connections, TimeProvider.System);
        await new SqlStateStore(_db.Connections, TimeProvider.System).EnsureSchemaAsync(ct);
        await store.EnsureSchemaAsync(ct);

        var regras = new SyncOptions { FeriasJanelaDias = 60 };
        await store.AddConfigurationVersionAsync("default", active: true, SyncOptionsJson.Serialize(regras), empresas, "teste", "teste", ct);
        var protector = new TokenProtector(new TokenProtectionOptions { ChaveBase64 = ChaveTokens });
        foreach (var (cdempresa, token) in tokens)
        {
            await store.AddEmpresaTokenAsync(cdempresa, protector.Protect(token), "teste", ct);
        }
    }

    /// <summary>Atende a fila solidesdp.comando como o serviço faria entre duas execuções.</summary>
    public async Task<int> ProcessCommandsAsync(CancellationToken ct = default)
    {
        await using var provider = BuildWorker();
        var commands = provider.GetRequiredService<CommandProcessor>();
        await commands.RecoverAsync(ct);
        return await commands.ProcessPendingAsync(ct);
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
        await _root.DisposeAsync();
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
