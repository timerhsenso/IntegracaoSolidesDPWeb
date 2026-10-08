using Microsoft.AspNetCore.Routing;

namespace SolidesDP.Fake.Tests.Contracts;

/// <summary>Rotas implementadas e respostas validadas, recursivamente, contra o Swagger 2.0 do fornecedor.</summary>
public sealed class ContractTests : FakeTestBase
{
    private static readonly SwaggerContract Contract = SwaggerContract.Load();

    /// <summary>Todas as rotas que o fake deve implementar (metodo, caminho exatamente como no Swagger).</summary>
    private static readonly string[] ExpectedRoutes =
    [
        "GET /test",
        "GET /companies",
        "POST /companies",
        "POST /job-role/register",
        "GET /job-role/find",
        "GET /job-role/find-all",
        "POST /workplace/register",
        "GET /workplace/find",
        "GET /workplace/find-all",
        "GET /work-schedule",
        "GET /work-schedule/default",
        "GET /v2/punch-rule",
        "POST /employee/register",
        "GET /employee/find",
        "GET /employee/find-all",
        "POST /employee/dismiss",
        "GET /adjustment-reason/find-all",
        "POST /adjustment/register",
        "POST /adjustment/register/1.1",
        "GET /adjustment/find-all",
        "GET /adjustment/{id}",
        "PUT /adjustment/update/{id}",
        "PUT /adjustment/{id}",
    ];

    [Fact]
    public void Implemented_routes_are_exactly_the_expected_list_and_all_exist_in_the_swagger()
    {
        var implemented = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !e.RoutePattern.RawText!.StartsWith("/_fake", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .ToList();

        implemented.Should().BeEquivalentTo(ExpectedRoutes);

        foreach (var route in ExpectedRoutes)
        {
            var parts = route.Split(' ');
            Contract.HasOperation(parts[0], parts[1]).Should().BeTrue($"{route} deve existir em `paths` do Swagger");
        }
    }

    [Fact]
    public async Task Get_test_returns_a_string()
    {
        using var response = await GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Contract.ResponseSchema("GET", "/test")!["type"]!.GetValue<string>().Should().Be("string");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Companies_conform()
    {
        var page = await GetJsonAsync("/companies");
        AssertResponse("GET", "/companies", page);

        var created = await PostJsonAsync("/companies", new JsonObject { ["cnpj"] = "11222333000181", ["fantasyName"] = "NOVA FILIAL", ["socialReason"] = "NOVA LTDA" });
        AssertResponse("POST", "/companies", created);
    }

    [Fact]
    public async Task Job_roles_conform()
    {
        var created = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1", ["cbo"] = "212405" });
        AssertResponse("POST", "/job-role/register", created);

        AssertResponse("GET", "/job-role/find", await GetJsonAsync("/job-role/find?externalId=J-1"));
        AssertResponse("GET", "/job-role/find-all", await GetJsonAsync("/job-role/find-all"));
    }

    [Fact]
    public async Task Workplaces_conform()
    {
        var created = await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA CENTRO", ["externalId"] = "W-1" });
        AssertResponse("POST", "/workplace/register", created);

        AssertResponse("GET", "/workplace/find", await GetJsonAsync("/workplace/find?externalId=W-1"));
        AssertResponse("GET", "/workplace/find-all", await GetJsonAsync("/workplace/find-all?active=true"));
    }

    [Fact]
    public async Task Work_schedules_and_punch_rules_conform()
    {
        AssertResponse("GET", "/work-schedule", await GetJsonAsync("/work-schedule"));
        AssertResponse("GET", "/work-schedule/default", await GetJsonAsync("/work-schedule/default"));
        AssertResponse("GET", "/v2/punch-rule", await GetJsonAsync("/v2/punch-rule"));
    }

    [Fact]
    public async Task Employee_endpoints_conform()
    {
        await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1" });
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA", ["externalId"] = "W-1" });

        var payload = Payloads.Employee("E-1", "12345678901");
        payload["pis"] = "12345678901";
        payload["email"] = "e1@example.com";
        payload["gender"] = "FEMININO";
        payload["birthDate"] = Payloads.Jan2024 - (9000 * Payloads.Day);
        payload["jobRoleExternalId"] = "J-1";
        payload["workplaceExternalId"] = "W-1";
        payload["company"] = 3001;
        payload["recordsPunch"] = true;

        var registered = await RegisterEmployeeAsync(payload);
        AssertResponse("POST", "/employee/register", registered);
        Contract.AssertConforms(registered["body"], "EmployeeReturnDTO"); // o corpo do envelope e o EmployeeReturnDTO (com pin)
        registered["body"]!["pin"].Should().NotBeNull();

        AssertResponse("GET", "/employee/find", await GetJsonAsync("/employee/find?externalId=E-1"));
        AssertResponse("GET", "/employee/find-all", await GetJsonAsync("/employee/find-all"));

        var dismissed = await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));
        AssertResponse("POST", "/employee/dismiss", dismissed);
        Contract.AssertConforms(dismissed["body"], "EmployeeReturnWithoutPinDTO");
        AssertResponse("GET", "/employee/find", await GetJsonAsync("/employee/find?externalId=E-1"));
        AssertResponse("GET", "/employee/find-all", await GetJsonAsync("/employee/find-all?showFired=1"));
    }

    [Fact]
    public async Task Adjustment_endpoints_conform()
    {
        var employeeId = await CreateEmployeeAsync("E-1");

        AssertResponse("GET", "/adjustment-reason/find-all", await GetJsonAsync("/adjustment-reason/find-all"));

        var registered = await RegisterVacationAsync("E-1", Payloads.Jan2024, Payloads.Jan2024 + (9 * Payloads.Day));
        AssertResponse("POST", "/adjustment/register", registered);

        var registered11 = await PostJsonAsync("/adjustment/register/1.1", Payloads.Vacation11("E-1", Payloads.Jan2024 + (20 * Payloads.Day), Payloads.Jan2024 + (25 * Payloads.Day)));
        AssertResponse("POST", "/adjustment/register/1.1", registered11);

        var id = (long)registered["entity"]!["id"]!;
        AssertResponse("GET", "/adjustment/find-all", await GetJsonAsync($"/adjustment/find-all?employeeId={employeeId}"));
        AssertResponse("GET", "/adjustment/{id}", await GetJsonAsync($"/adjustment/{id}"));

        var updatedV2 = await PutJsonAsync($"/adjustment/update/{id}", new JsonObject { ["observation"] = "ajuste", ["status"] = "PENDENTE" });
        AssertResponse("PUT", "/adjustment/update/{id}", updatedV2);

        var updated = await PutJsonAsync($"/adjustment/{id}", new JsonObject { ["status"] = "APROVADO", ["origem"] = "Integração" });
        AssertResponse("PUT", "/adjustment/{id}", updated);
        Contract.AssertConforms(updated["body"], "AdjustmentReasonRecordResponseDTO");
    }

    [Fact]
    public async Task Error_shapes_conform_to_the_swagger_definitions()
    {
        // ResponseEntity (employee/register): HTTP 200 + {body, statusCode, statusCodeValue}
        var employeeError = await PostJsonAsync("/employee/register", new JsonObject { ["name"] = "SEM DATAS" });
        Contract.AssertConforms(employeeError, "ResponseEntity");
        employeeError["statusCode"]!.GetValue<string>().Should().Be("BAD_REQUEST");
        employeeError["statusCodeValue"]!.GetValue<int>().Should().Be(400);

        // Adjustment*ResponseDTO: HTTP 200 + {registered:false, message}
        var adjustmentError = await PostJsonAsync("/adjustment/register", Payloads.Vacation("NAO-EXISTE", Payloads.Jan2024, Payloads.Jan2024));
        Contract.AssertConforms(adjustmentError, "AdjustmentLaunchOnlyResponseDTO");
        adjustmentError["registered"]!.GetValue<bool>().Should().BeFalse();

        var updateError = await PutJsonAsync("/adjustment/update/999999", new JsonObject { ["observation"] = "x" });
        Contract.AssertConforms(updateError, "AdjustmentLaunchResponseDTO");

        var putError = await PutJsonAsync("/adjustment/999999", new JsonObject { ["status"] = "APROVADO" });
        Contract.AssertConforms(putError, "ResponseEntity");
        putError["statusCodeValue"]!.GetValue<int>().Should().Be(404);
    }

    [Fact]
    public async Task Success_envelope_status_name_matches_its_numeric_value()
    {
        var created = await RegisterEmployeeAsync(Payloads.Employee("E-1"));
        created["statusCode"]!.GetValue<string>().Should().Be("CREATED");
        created["statusCodeValue"]!.GetValue<int>().Should().Be(201);

        var updated = await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true);
        updated["statusCode"]!.GetValue<string>().Should().Be("OK");
        updated["statusCodeValue"]!.GetValue<int>().Should().Be(200);
    }

    [Fact]
    public void The_contract_helper_itself_catches_violations()
    {
        // Garante que o helper de teste nao e permissivo demais (senao os testes acima nao provariam nada).
        Contract.Validate(JsonNode.Parse("""{"id":1,"inventedProperty":true}"""), "WorkplaceReturnDTO").Should().ContainSingle(v => v.Contains("inventedProperty", StringComparison.Ordinal));
        Contract.Validate(JsonNode.Parse("""{"id":"x"}"""), "WorkplaceReturnDTO").Should().NotBeEmpty();
        Contract.Validate(JsonNode.Parse("""{"gender":"OUTRO"}"""), "EmployeeReturnWithoutPinDTO").Should().NotBeEmpty();
        Contract.Validate(JsonNode.Parse("""{"content":[{"id":1,"nope":1}]}"""), SwaggerContract.Generic("Page", "JobRoleDTO")).Should().NotBeEmpty();
        Contract.Validate(JsonNode.Parse("""{"statusCode":"NOT_A_STATUS"}"""), "ResponseEntity").Should().NotBeEmpty();
        Contract.Validate(JsonNode.Parse("""{"statusCode":"OK","statusCodeValue":200}"""), "ResponseEntity").Should().BeEmpty();
    }

    private void AssertResponse(string method, string pathTemplate, JsonNode body)
    {
        var definition = Contract.ResponseDefinitionName(method, pathTemplate)
            ?? throw new InvalidOperationException($"{method} {pathTemplate} nao declara schema de resposta 200");
        Contract.AssertConforms(body, definition);
    }
}
