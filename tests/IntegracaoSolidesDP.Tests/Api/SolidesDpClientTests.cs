using System.Net;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IntegracaoSolidesDP.Tests.Api;

/// <summary>Formato exato das requisições e o comportamento de retry, contra um servidor HTTP controlado.</summary>
public sealed class SolidesDpClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ServiceProvider _provider;
    private readonly ISolidesDpClient _client;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public SolidesDpClientTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SolidesDpOptions
        {
            BaseUrl = _server.Url!,
            Token = "abc123",
            TimeoutSeconds = 2,
        }));
        services.AddSolidesDpClient(retry =>
        {
            retry.Retry.Delay = TimeSpan.FromMilliseconds(1);
            retry.Retry.UseJitter = false;
            retry.Retry.MaxDelay = TimeSpan.FromMilliseconds(50);
        });
        _provider = services.BuildServiceProvider();
        _client = _provider.GetRequiredService<ISolidesDpClient>();
    }

    [Fact]
    public async Task Sends_the_token_as_basic_authorization_exactly_as_documented()
    {
        _server.Given(Request.Create().WithPath("/test").UsingGet().WithHeader("Authorization", "Basic abc123"))
            .RespondWith(Response.Create().WithBody("OK"));

        var result = await _client.TestAsync(Ct);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Employee_register_is_an_upsert_with_allow_update_and_skip_unified_sync()
    {
        _server.Given(Request.Create().WithPath("/employee/register").UsingPost()
                .WithParam("allowUpdate", "true").WithParam("skipUnifiedSync", "true"))
            .RespondWith(Response.Create().WithBodyAsJson(new { body = new { id = 77, externalId = "1-00000001" }, statusCode = "CREATED", statusCodeValue = 201 }));

        var result = await _client.RegisterEmployeeAsync(new EmployeeRequest { ExternalId = "1-00000001", Name = "X" }, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(77);
        var sent = _server.LogEntries.Single().RequestMessage!.Body;
        sent.Should().Be("""{"externalId":"1-00000001","name":"X"}""");
    }

    [Fact]
    public async Task Rate_limit_is_retried_honouring_retry_after()
    {
        _server.Given(Request.Create().WithPath("/workplace/register").UsingPost()).InScenario("rl").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));
        _server.Given(Request.Create().WithPath("/workplace/register").UsingPost()).InScenario("rl").WhenStateIs("ok")
            .RespondWith(Response.Create().WithBodyAsJson(new { id = 5, externalId = "1-1", name = "N" }));

        var result = await _client.RegisterWorkplaceAsync(new WorkplaceRequest("N", "1-1"), Ct);

        result.IsSuccess.Should().BeTrue();
        _server.LogEntries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Vacation_create_is_never_retried_because_it_has_no_idempotency_key()
    {
        _server.Given(Request.Create().WithPath("/adjustment/register/1.1").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var result = await _client.RegisterAdjustmentAsync(new AdjustmentRegisterRequest { EmployeeExternalId = "1-1" }, Ct);

        result.Outcome.Should().Be(ApiOutcome.TransportError);
        _server.LogEntries.Should().ContainSingle();
    }

    [Fact]
    public async Task Job_role_create_is_never_retried()
    {
        _server.Given(Request.Create().WithPath("/job-role/register").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        await _client.RegisterJobRoleAsync(new JobRoleRequest("ANALISTA", "002", null), Ct);

        _server.LogEntries.Should().ContainSingle();
    }

    [Fact]
    public async Task Validation_errors_are_not_retried()
    {
        _server.Given(Request.Create().WithPath("/employee/register").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { message = "workplace inexistente" }));

        var result = await _client.RegisterEmployeeAsync(new EmployeeRequest { ExternalId = "1-1" }, Ct);

        result.Outcome.Should().Be(ApiOutcome.Rejected);
        result.Message.Should().Be("workplace inexistente");
        _server.LogEntries.Should().ContainSingle();
    }

    [Fact]
    public async Task Pages_are_followed_until_the_last_one()
    {
        _server.Given(Request.Create().WithPath("/companies").UsingGet().WithParam("pageNumber", "0"))
            .RespondWith(Response.Create().WithBodyAsJson(new
            {
                content = Enumerable.Range(1, 100).Select(i => new { id = i, cnpj = $"{i:D14}" }),
                last = false,
                number = 0,
                totalPages = 2,
            }));
        _server.Given(Request.Create().WithPath("/companies").UsingGet().WithParam("pageNumber", "1"))
            .RespondWith(Response.Create().WithBodyAsJson(new { content = new[] { new { id = 101, cnpj = "00594807000108" } }, last = true, number = 1, totalPages = 2 }));

        var result = await _client.GetCompaniesAsync(Ct);

        result.Value.Should().HaveCount(101);
    }

    [Fact]
    public async Task Punch_rules_are_unwrapped_from_base_item()
    {
        _server.Given(Request.Create().WithPath("/v2/punch-rule").UsingGet())
            .RespondWith(Response.Create().WithBodyAsJson(new
            {
                code = 200,
                status = "OK",
                item = new { content = new[] { new { id = 2001, externalId = "REGRA-PADRAO", standard = true } }, last = true },
            }));

        var result = await _client.GetPunchRulesAsync(Ct);

        result.Value.Should().ContainSingle().Which.ExternalId.Should().Be("REGRA-PADRAO");
    }

    [Fact]
    public async Task Created_adjustment_id_comes_from_entity()
    {
        _server.Given(Request.Create().WithPath("/adjustment/register/1.1").UsingPost())
            .RespondWith(Response.Create().WithBodyAsJson(new { registered = true, message = "ok", entity = new { id = 991, observation = "RHSenso:x" } }));

        var result = await _client.RegisterAdjustmentAsync(new AdjustmentRegisterRequest { EmployeeExternalId = "1-1" }, Ct);

        result.Value!.Id.Should().Be(991);
    }

    [Fact]
    public async Task Unauthorized_is_reported_as_such()
    {
        _server.Given(Request.Create().WithPath("/test").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized));

        (await _client.TestAsync(Ct)).Outcome.Should().Be(ApiOutcome.Unauthorized);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _server.Stop();
        _server.Dispose();
    }
}
