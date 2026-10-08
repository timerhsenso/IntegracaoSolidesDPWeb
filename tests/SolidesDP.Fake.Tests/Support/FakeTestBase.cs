using System.Text;

namespace SolidesDP.Fake.Tests.Support;

/// <summary>Base dos testes: cada teste ganha o seu proprio fake (xunit cria uma instancia da classe por teste).</summary>
public abstract class FakeTestBase : IAsyncDisposable
{
    protected FakeTestBase(IReadOnlyDictionary<string, string?>? settings = null) => Host = new FakeHost(settings);

    protected FakeHost Host { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    protected static Uri Rel(string url) => new(url, UriKind.Relative);

    protected static StringContent JsonBody(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    protected Task<HttpResponseMessage> GetAsync(string url) => Host.Api.GetAsync(Rel(url), Ct);

    protected Task<HttpResponseMessage> PostAsync(string url, JsonNode body) => Host.Api.PostAsync(Rel(url), JsonBody(body), Ct);

    protected Task<HttpResponseMessage> PutAsync(string url, JsonNode body) => Host.Api.PutAsync(Rel(url), JsonBody(body), Ct);

    protected Task<JsonNode> PutJsonAsync(string url, JsonNode body) => SendJsonAsync(PutAsync(url, body));

    protected static async Task<JsonNode> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Resposta sem JSON: " + text);
    }

    /// <summary>GET esperando 200 e devolvendo o JSON.</summary>
    protected async Task<JsonNode> GetJsonAsync(string url)
    {
        using var response = await GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    /// <summary>POST esperando HTTP 200 e devolvendo o JSON.</summary>
    protected Task<JsonNode> PostJsonAsync(string url, JsonNode body) => SendJsonAsync(PostAsync(url, body));

    private static async Task<JsonNode> SendJsonAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    /// <summary>Registra um colaborador (<c>POST /employee/register</c>) e devolve o envelope <c>ResponseEntity</c>.</summary>
    protected Task<JsonNode> RegisterEmployeeAsync(JsonObject payload, bool allowUpdate = false) =>
        PostJsonAsync("/employee/register" + (allowUpdate ? "?allowUpdate=true" : string.Empty), payload);

    /// <summary>Registra um colaborador valido e devolve o id gerado.</summary>
    protected async Task<long> CreateEmployeeAsync(string externalId, string? cpf = null, Action<JsonObject>? customize = null)
    {
        var payload = Payloads.Employee(externalId, cpf);
        customize?.Invoke(payload);
        var response = await RegisterEmployeeAsync(payload);
        Assert.Equal(201, (int)response["statusCodeValue"]!);
        return (long)response["body"]!["id"]!;
    }

    /// <summary>Lanca ferias (<c>POST /adjustment/register</c>) para o colaborador (por externalId).</summary>
    protected Task<JsonNode> RegisterVacationAsync(string employeeExternalId, long start, long end) =>
        PostJsonAsync("/adjustment/register", Payloads.Vacation(employeeExternalId, start, end));

    protected async Task<Domain.FakeState> StateAsync() => await Host.Admin.GetStateAsync(Ct);

    protected Task SetBehaviorAsync(Action<FakeBehavior> change) => Host.Admin.UpdateBehaviorAsync(change, Ct);

    /// <summary>Confere um erro no envelope <c>ResponseEntity</c> (HTTP 200 + statusCodeValue + body.error).</summary>
    protected static void AssertEnvelopeError(JsonNode envelope, string code, int status)
    {
        envelope["statusCodeValue"]!.GetValue<int>().Should().Be(status);
        envelope["statusCode"]!.GetValue<string>().Should().Be(SpringName(status));
        envelope["body"]!["error"]!.GetValue<string>().Should().Be(code);
        envelope["body"]!["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Confere o erro de lancamento (<c>{registered:false, message}</c>).</summary>
    protected static void AssertLaunchError(JsonNode json)
    {
        json["registered"]!.GetValue<bool>().Should().BeFalse();
        json["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        json.AsObject().Select(p => p.Key).Should().BeEquivalentTo("registered", "message");
    }

    /// <summary>Confere o corpo de erro no estilo Spring (<c>timestamp,status,error,message,path</c>).</summary>
    protected static void AssertSpringError(JsonNode body, int status, string? path = null)
    {
        body.AsObject().Select(p => p.Key).Should().BeEquivalentTo("timestamp", "status", "error", "message", "path");
        body["status"]!.GetValue<int>().Should().Be(status);
        body["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["message"]!.GetValue<string>().Should().NotBeNull();
        if (path is not null)
        {
            body["path"]!.GetValue<string>().Should().Be(path);
        }
    }

    protected static string SpringName(int status) => status switch
    {
        400 => "BAD_REQUEST",
        404 => "NOT_FOUND",
        409 => "CONFLICT",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Faz uma requisicao anonima com o header Authorization informado (ou sem header).</summary>
    protected async Task<HttpResponseMessage> SendWithAuthorizationAsync(string path, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Rel(path));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await Host.Anonymous.SendAsync(request, Ct);
    }
}
