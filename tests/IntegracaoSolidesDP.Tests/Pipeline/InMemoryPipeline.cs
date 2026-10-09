using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.Source;
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

namespace IntegracaoSolidesDP.Tests.Pipeline;

/// <summary>
/// O pipeline inteiro (mesmo registro de DI do Program) contra o fake in-process, com o RHSenso e o estado em
/// memória: cobre a regra de identidade por CPF sem precisar do SQL Server.
/// </summary>
internal sealed class InMemoryPipeline : IAsyncDisposable
{
    public const string Token = "fake-token";
    public const string Token2 = "fake-token-2";

    private readonly WebApplicationFactory<FakeApi> _root = new();
    private readonly WebApplicationFactory<FakeApi> _fake;
    private readonly string _reports = Directory.CreateTempSubdirectory("solidesdp-mem").FullName;

    public InMemoryPipeline()
    {
        _fake = _root.WithWebHostBuilder(b => b
            .UseSetting("Fake:Tokens:0", Token)
            .UseSetting("Fake:Tokens:1", Token2)
            .UseSetting("Fake:IsolarContasPorToken", "true"));
        Fake = new FakeAdminClient(_fake.CreateClient());
    }

    public FakeAdminClient Fake { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(-3)));

    public MemorySource Source { get; } = new();

    public MemoryState State { get; } = new();

    public MemoryManagement Management { get; } = new();

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
        ["ConnectionStrings:Rhu"] = "Server=nao-usado;Database=nao-usado",
    };

    public HttpClient DpAsHr(string token = Token)
    {
        var client = _fake.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        return client;
    }

    public async Task<RunSummary> RunAsync(bool? dryRun = null, int? empresa = null, CancellationToken ct = default)
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SyncPipeline>().RunAsync(new RunRequest("teste", dryRun, Cdempresa: empresa), ct);
    }

    public async Task<IReadOnlyList<SolidesDP.Fake.Domain.RecordedRequest>> WritesAsync(CancellationToken ct = default) =>
        (await Fake.GetRequestsAsync(ct)).Where(r => r.Method is "POST" or "PUT" or "DELETE").ToList();

    private ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(Settings) { ["Sync:ReportDirectory"] = _reports })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Development", ApplicationName = "memoria" });
        services.AddIntegracaoSolidesDP(configuration);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<ISourceReader>(Source);
        services.AddSingleton<IStateStore>(State);
        services.AddSingleton<IManagementStore>(Management);
        services.AddSingleton<ITokenProtector>(new PlainTokens());

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
            // Relatórios temporários.
        }
    }

    /// <summary>Os testes guardam o token "cifrado" como texto; a cifra de verdade tem testes próprios.</summary>
    private sealed class PlainTokens : ITokenProtector
    {
        public byte[] Protect(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Unprotect(byte[] protegido) => System.Text.Encoding.UTF8.GetString(protegido);
    }
}

internal sealed class MemorySource : ISourceReader
{
    public List<EmployeeRow> Employees { get; } = [];

    public List<JobRoleRow> JobRoles { get; } = [new() { Cdcargo = "00100", Descricao = "ANALISTA DE SISTEMAS", Cbo = "212405" }];

    public List<WorkplaceRow> Workplaces { get; } =
        [new() { Cdempresa = 1, Cdfilial = 1, NomeFantasia = "ADN MATRIZ", Descricao = "ADN MATRIZ", Cnpj = "00594807000108", Ativa = true }];

    public List<EmpresaRow> Empresas { get; } = [new() { Cdempresa = 1, Nome = "ADN" }];

    public List<VacationRow> Vacations { get; } = [];

    public Task<IReadOnlyList<EmployeeRow>> ReadEmployeesAsync(IReadOnlyCollection<int> tipos, IReadOnlyCollection<int> empresas, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EmployeeRow>>(Employees
            .Where(e => tipos.Contains(e.TipoColaborador) && (empresas.Count == 0 || empresas.Contains(e.Cdempresa)))
            .ToList());

    public Task<IReadOnlyList<JobRoleRow>> ReadJobRolesAsync(IReadOnlyCollection<string> codigos, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JobRoleRow>>(JobRoles.Where(j => codigos.Contains(j.Cdcargo)).ToList());

    public Task<IReadOnlyList<WorkplaceRow>> ReadWorkplacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkplaceRow>>(Workplaces.ToList());

    public Task<IReadOnlyList<EmpresaRow>> ReadEmpresasAtivasAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<EmpresaRow>>(Empresas.ToList());

    public Task<IReadOnlyList<VacationRow>> ReadVacationsEndingFromAsync(DateOnly desde, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VacationRow>>(Vacations.Where(v => DateOnly.FromDateTime(v.Fim) >= desde).ToList());

    public Task<IReadOnlySet<Guid>> ExistingVacationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(Vacations.Select(v => v.Id).Where(ids.Contains).ToHashSet());
}

internal sealed class MemoryState : IStateStore
{
    private readonly Dictionary<(int, string, string), EntityState> _entities = [];
    private readonly Dictionary<(int, Guid), VacationState> _vacations = [];

    public List<(Guid RunId, int? Cdempresa, bool DryRun, string Status, string? Error)> Runs { get; } = [];

    public Dictionary<Guid, List<RunItem>> Items { get; } = [];

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IAsyncDisposable?> TryAcquireRunLockAsync(string instanceName, int cdempresa, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(new NoLock());

    public Task VerifyKeySchemeAsync(string scheme, CancellationToken ct) => Task.CompletedTask;

    public Task<Guid> StartRunAsync(string instanceName, int? cdempresa, bool dryRun, string trigger, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        Runs.Add((id, cdempresa, dryRun, RunStatuses.Running, null));
        return Task.FromResult(id);
    }

    public Task FinishRunAsync(Guid runId, string status, string summaryJson, string? errorMessage, CancellationToken ct)
    {
        var index = Runs.FindIndex(r => r.RunId == runId);
        Runs[index] = Runs[index] with { Status = status, Error = errorMessage };
        return Task.CompletedTask;
    }

    public Task<int> FailInterruptedRunsAsync(string instanceName, int cdempresa, CancellationToken ct) => Task.FromResult(0);

    public Task AddItemsAsync(Guid runId, IReadOnlyCollection<RunItem> items, CancellationToken ct)
    {
        Items[runId] = items.ToList();
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, EntityState>> LoadEntityStatesAsync(int cdempresa, string entityType, CancellationToken ct) =>
        Task.FromResult(_entities.Where(e => e.Key.Item1 == cdempresa && e.Key.Item2 == entityType)
            .ToDictionary(e => e.Key.Item3, e => e.Value, StringComparer.Ordinal));

    public Task UpsertEntityStateAsync(int cdempresa, EntityState state, CancellationToken ct)
    {
        _entities[(cdempresa, state.EntityType, state.ExternalId)] = state;
        return Task.CompletedTask;
    }

    public Task<Dictionary<Guid, VacationState>> LoadVacationStatesAsync(int cdempresa, CancellationToken ct) =>
        Task.FromResult(_vacations.Where(v => v.Key.Item1 == cdempresa).ToDictionary(v => v.Key.Item2, v => v.Value));

    public Task UpsertVacationStateAsync(int cdempresa, VacationState state, CancellationToken ct)
    {
        _vacations[(cdempresa, state.Feria2Id)] = state;
        return Task.CompletedTask;
    }

    public EntityState? Employee(int cdempresa, string cpf) => _entities.GetValueOrDefault((cdempresa, EntityTypes.Employee, cpf));

    private sealed class NoLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Gestão em memória: só o que o serviço lê numa execução (configuração vigente e tokens).</summary>
internal sealed class MemoryManagement : IManagementStore
{
    public ConfigurationVersion? Current { get; set; }

    public Dictionary<int, EmpresaToken> Tokens { get; } = [];

    public void Configure(IReadOnlyList<EmpresaConfiguracao> empresas, IReadOnlyDictionary<int, string> tokens, string syncJson)
    {
        Current = new ConfigurationVersion { Id = 1, Version = 1, Active = true, SyncJson = syncJson, CreatedBy = "teste", Empresas = empresas };
        foreach (var (empresa, token) in tokens)
        {
            Tokens[empresa] = new EmpresaToken { Cdempresa = empresa, TokenCifrado = System.Text.Encoding.UTF8.GetBytes(token), CriadoPor = "teste" };
        }
    }

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<ConfigurationVersion?> GetCurrentConfigurationAsync(string instanceName, CancellationToken ct) => Task.FromResult(Current);

    public Task SeedConfigurationAsync(string instanceName, string syncJson, CancellationToken ct)
    {
        Current ??= new ConfigurationVersion { Id = 1, Version = 1, Active = true, SyncJson = syncJson, CreatedBy = ManagementUsers.System };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<int, EmpresaToken>> GetEmpresaTokensAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, EmpresaToken>>(Tokens);

    public Task TagRunAsync(Guid runId, string? requestedBy, int? configVersion, CancellationToken ct) => Task.CompletedTask;

    public Task<int> AddConfigurationVersionAsync(string instanceName, bool active, string syncJson, string? note, string createdBy, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, IReadOnlyList<EmpresaConfiguracao> empresas, string? note, string createdBy, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task AddEmpresaTokenAsync(int cdempresa, byte[]? tokenCifrado, string createdBy, CancellationToken ct) => throw new NotSupportedException();

    public Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, CancellationToken ct) => throw new NotSupportedException();

    public Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, int? cdempresa, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ClaimedCommand?> ClaimNextCommandAsync(string instanceName, CancellationToken ct) => Task.FromResult<ClaimedCommand?>(null);

    public Task CompleteCommandAsync(long commandId, string status, Guid? runId, string? result, CancellationToken ct) => Task.CompletedTask;

    public Task<int> FailInterruptedCommandsAsync(string instanceName, CancellationToken ct) => Task.FromResult(0);
}
