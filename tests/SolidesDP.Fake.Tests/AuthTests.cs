namespace SolidesDP.Fake.Tests;

public sealed class AuthTests : FakeTestBase
{
    [Fact]
    public async Task Missing_authorization_header_is_401_with_a_spring_style_body()
    {
        using var response = await SendWithAuthorizationAsync("/test", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertSpringError(await ReadAsync(response), 401, "/test");
    }

    [Theory]
    [InlineData("Basic wrong-token")]
    [InlineData("Bearer fake-token")]
    [InlineData("fake-token")] // token puro: so vale com RequireBasicPrefix=false
    [InlineData("Basic ")]
    public async Task Invalid_credentials_are_401(string authorization)
    {
        using var response = await SendWithAuthorizationAsync("/employee/find-all", authorization);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertSpringError(await ReadAsync(response), 401, "/employee/find-all");
    }

    [Theory]
    [InlineData("Basic fake-token")]
    [InlineData("basic fake-token")]
    [InlineData("BASIC   fake-token ")]
    public async Task Basic_token_is_accepted(string authorization)
    {
        using var response = await SendWithAuthorizationAsync("/test", authorization);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bare_token_is_accepted_only_when_the_basic_prefix_is_not_required()
    {
        await SetBehaviorAsync(b => b.RequireBasicPrefix = false);

        using var bare = await SendWithAuthorizationAsync("/test", "fake-token");
        using var prefixed = await SendWithAuthorizationAsync("/test", "Basic fake-token");
        using var wrong = await SendWithAuthorizationAsync("/test", "other-token");

        bare.StatusCode.Should().Be(HttpStatusCode.OK);
        prefixed.StatusCode.Should().Be(HttpStatusCode.OK);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_path_is_401_without_credentials_and_a_spring_404_with_them()
    {
        using var anonymous = await SendWithAuthorizationAsync("/nao-existe", null);
        using var authenticated = await GetAsync("/nao-existe");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertSpringError(await ReadAsync(authenticated), 404, "/nao-existe");
    }

    [Fact]
    public async Task Wrong_method_is_a_spring_405()
    {
        using var response = await PostAsync("/test", new JsonObject());

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        AssertSpringError(await ReadAsync(response), 405, "/test");
    }

    [Fact]
    public async Task Admin_endpoints_need_no_credentials()
    {
        var state = await Host.Admin.GetStateAsync(Ct);

        state.Companies.Should().HaveCount(14);
    }
}

public sealed class CustomTokenAuthTests() : FakeTestBase(new Dictionary<string, string?>
{
    ["Fake:Tokens:0"] = "token-a",
    ["Fake:Tokens:1"] = "token-b",
})
{
    [Theory]
    [InlineData("Basic token-a", HttpStatusCode.OK)]
    [InlineData("Basic token-b", HttpStatusCode.OK)]
    [InlineData("Basic fake-token", HttpStatusCode.Unauthorized)] // o padrao so vale quando nada e configurado
    public async Task Only_the_configured_tokens_are_valid(string authorization, HttpStatusCode expected)
    {
        using var response = await SendWithAuthorizationAsync("/test", authorization);

        response.StatusCode.Should().Be(expected);
    }
}
