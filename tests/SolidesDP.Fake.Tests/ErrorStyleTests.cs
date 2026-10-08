namespace SolidesDP.Fake.Tests;

/// <summary>Os tres formatos de erro (ResponseEntity, Launch, Spring) em cada <see cref="ErrorStyle"/>.</summary>
public sealed class ErrorStyleTests : FakeTestBase
{
    [Fact]
    public async Task ResponseEntity_style_puts_errors_of_response_entity_endpoints_in_an_http_200_body()
    {
        await CreateEmployeeAsync("E-1");

        using var register = await PostAsync("/employee/register", Payloads.Employee("E-1"));
        using var dismiss = await PostAsync("/employee/dismiss", Payloads.Dismiss("NAO-EXISTE"));
        using var put = await PutAsync("/adjustment/424242", new JsonObject { ["status"] = "APROVADO" });

        foreach (var response in new[] { register, dismiss, put })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var registerBody = await ReadAsync(register);
        AssertEnvelopeError(registerBody, "already_exists", 409);
        registerBody.AsObject().Select(p => p.Key).Should().BeEquivalentTo("body", "statusCode", "statusCodeValue");
        registerBody["body"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo("message", "error");
        AssertEnvelopeError(await ReadAsync(dismiss), "not_found", 404);
        AssertEnvelopeError(await ReadAsync(put), "not_found", 404);
    }

    [Fact]
    public async Task ResponseEntity_style_puts_launch_errors_in_an_http_200_body()
    {
        using var register = await PostAsync("/adjustment/register", Payloads.Vacation("NAO-EXISTE", Payloads.Jan2024, Payloads.Jan2024));
        using var register11 = await PostAsync("/adjustment/register/1.1", Payloads.Vacation11("NAO-EXISTE", Payloads.Jan2024, Payloads.Jan2024));
        using var update = await PutAsync("/adjustment/update/424242", new JsonObject { ["observation"] = "x" });

        foreach (var response in new[] { register, register11, update })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertLaunchError(await ReadAsync(response));
        }
    }

    [Fact]
    public async Task ResponseEntity_style_keeps_other_endpoints_as_http_4xx_with_a_spring_body()
    {
        await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "A", ["externalId"] = "J-1" });

        using var duplicateJobRole = await PostAsync("/job-role/register", new JsonObject { ["description"] = "A", ["externalId"] = "J-1" });
        using var missingEmployee = await GetAsync("/employee/find?externalId=NAO-EXISTE");
        using var missingAdjustment = await GetAsync("/adjustment/424242");

        duplicateJobRole.StatusCode.Should().Be(HttpStatusCode.Conflict);
        missingEmployee.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingAdjustment.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertSpringError(await ReadAsync(duplicateJobRole), 409, "/job-role/register");
        AssertSpringError(await ReadAsync(missingEmployee), 404, "/employee/find");
    }

    [Fact]
    public async Task Http_style_turns_every_business_error_into_a_4xx_with_a_spring_body()
    {
        await SetBehaviorAsync(b => b.ErrorStyle = ErrorStyle.Http);
        await CreateEmployeeAsync("E-1", "11111111111");
        await RegisterVacationAsync("E-1", Payloads.Jan2024, Payloads.Jan2024 + Payloads.Day);

        var cases = new (string Name, Task<HttpResponseMessage> Call, HttpStatusCode Status)[]
        {
            ("already_exists", PostAsync("/employee/register", Payloads.Employee("E-1")), HttpStatusCode.Conflict),
            ("duplicate_cpf", PostAsync("/employee/register", Payloads.Employee("E-2", "11111111111")), HttpStatusCode.Conflict),
            ("required_field", PostAsync("/employee/register", new JsonObject { ["name"] = "X" }), HttpStatusCode.BadRequest),
            ("unknown_field", PostAsync("/employee/register", new JsonObject { ["foo"] = 1 }), HttpStatusCode.BadRequest),
            ("not_found", PostAsync("/employee/dismiss", Payloads.Dismiss("NAO-EXISTE")), HttpStatusCode.NotFound),
            ("overlap", PostAsync("/adjustment/register", Payloads.Vacation("E-1", Payloads.Jan2024, Payloads.Jan2024)), HttpStatusCode.Conflict),
            ("invalid_reference", PostAsync("/adjustment/register/1.1", Payloads.Vacation11("NAO-EXISTE", Payloads.Jan2024, Payloads.Jan2024)), HttpStatusCode.BadRequest),
            ("not_found", PutAsync("/adjustment/update/424242", new JsonObject { ["observation"] = "x" }), HttpStatusCode.NotFound),
            ("not_found", PutAsync("/adjustment/424242", new JsonObject { ["status"] = "APROVADO" }), HttpStatusCode.NotFound),
        };

        foreach (var (name, call, status) in cases)
        {
            using var response = await call;
            response.StatusCode.Should().Be(status, name);
            AssertSpringError(await ReadAsync(response), (int)status);
            response.Headers.GetValues("X-Fake-Error-Code").Should().Equal(name);
        }
    }

    [Fact]
    public async Task Http_style_does_not_change_successful_responses()
    {
        await SetBehaviorAsync(b => b.ErrorStyle = ErrorStyle.Http);

        var created = await RegisterEmployeeAsync(Payloads.Employee("E-1"));

        created["statusCode"]!.GetValue<string>().Should().Be("CREATED");
    }

    [Fact]
    public async Task The_error_code_is_exposed_in_a_response_header_in_every_style()
    {
        using var response = await PostAsync("/employee/dismiss", Payloads.Dismiss("NAO-EXISTE"));

        response.Headers.GetValues("X-Fake-Error-Code").Should().Equal("not_found");
    }
}

public sealed class ConfiguredErrorStyleTests() : FakeTestBase(new Dictionary<string, string?> { ["Fake:Behavior:ErrorStyle"] = "Http" })
{
    [Fact]
    public async Task Initial_behavior_comes_from_configuration()
    {
        using var response = await PostAsync("/employee/dismiss", Payloads.Dismiss("NAO-EXISTE"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Host.Admin.GetBehaviorAsync(Ct)).ErrorStyle.Should().Be(ErrorStyle.Http);
    }
}
