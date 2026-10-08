using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SolidesDP.Fake.Testing;

/// <summary>
/// O fake rodando num Kestrel real em uma porta loopback efemera, dentro do processo de teste. Use quando o teste precisa de
/// transporte de verdade, p.ex. para <see cref="Configuration.FaultKind.CommitThenDrop"/>: no Kestrel o cliente recebe uma
/// <see cref="HttpRequestException"/> (conexao resetada); no <c>TestServer</c> do <c>WebApplicationFactory</c> ele recebe uma
/// <see cref="OperationCanceledException"/>. A configuracao e hermetica: so o que for passado em <c>settings</c> vale
/// (chaves como <c>Fake:Tokens:0</c> e <c>Fake:Behavior:ErrorStyle</c>), sem appsettings nem variaveis de ambiente.
/// </summary>
public sealed class FakeServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Endereco base (ex.: <c>http://127.0.0.1:54321/</c>).</summary>
    public Uri BaseAddress { get; }

    /// <summary>Sobe o fake numa porta livre.</summary>
    /// <param name="settings">Configuracao (chaves de secao <c>Fake</c>) opcional.</param>
    /// <param name="cancellationToken">Cancelamento da subida.</param>
    public static async Task<FakeServer> StartAsync(IReadOnlyDictionary<string, string?>? settings = null, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FakeApi).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddFake();

        var app = builder.Build();
        app.UseFake();
        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, new Uri(address));
    }

    /// <summary>Cliente HTTP novo apontado para o fake; com <paramref name="token"/> envia <c>Authorization: Basic &lt;token&gt;</c>.</summary>
    public HttpClient CreateClient(string? token = "fake-token")
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return client;
    }

    /// <summary>Cliente dos endpoints administrativos (<c>/_fake</c>); descarte o <see cref="HttpClient"/> junto com o servidor.</summary>
    public FakeAdminClient CreateAdminClient() => new(CreateClient(token: null));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
