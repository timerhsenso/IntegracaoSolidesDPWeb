using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using SolidesDP.Fake.Testing;

namespace SolidesDP.Fake.Tests.Support;

/// <summary>Uma instancia isolada do fake em memoria (<c>WebApplicationFactory&lt;FakeApi&gt;</c>) com clientes prontos.</summary>
public sealed class FakeHost : IAsyncDisposable
{
    public const string Token = "fake-token";

    private readonly WebApplicationFactory<FakeApi> _factory;

    public FakeHost(IReadOnlyDictionary<string, string?>? settings = null)
    {
        _factory = new WebApplicationFactory<FakeApi>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }
        });

        Anonymous = _factory.CreateClient();
        Api = _factory.CreateClient();
        Api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Token);
        Admin = new FakeAdminClient(Anonymous);
    }

    /// <summary>Cliente autenticado (<c>Authorization: Basic fake-token</c>).</summary>
    public HttpClient Api { get; }

    /// <summary>Cliente sem credenciais (tambem usado pelos endpoints <c>/_fake</c>).</summary>
    public HttpClient Anonymous { get; }

    public FakeAdminClient Admin { get; }

    public IServiceProvider Services => _factory.Services;

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        Anonymous.Dispose();
        await _factory.DisposeAsync();
    }
}
