namespace SolidesDP.Fake.Tests;

public sealed class EmployeeQueryTests : FakeTestBase
{
    [Fact]
    public async Task Find_by_external_id_and_by_tangerino_id()
    {
        var id = await CreateEmployeeAsync("E-1", "11111111111", e => e["email"] = "a@b.com");

        var byExternal = await GetJsonAsync("/employee/find?externalId=E-1");
        var byId = await GetJsonAsync($"/employee/find?tangerinoId={id}");

        byExternal["id"]!.GetValue<long>().Should().Be(id);
        byExternal["email"]!.GetValue<string>().Should().Be("a@b.com");
        byExternal["fired"]!.GetValue<bool>().Should().BeFalse();
        byExternal["pin"].Should().BeNull(); // EmployeeReturnWithoutPinDTO
        byId.ToJsonString().Should().Be(byExternal.ToJsonString());
    }

    [Fact]
    public async Task Find_of_an_unknown_employee_is_a_spring_404_in_both_error_styles()
    {
        using var first = await GetAsync("/employee/find?externalId=NAO-EXISTE");
        await SetBehaviorAsync(b => b.ErrorStyle = ErrorStyle.Http);
        using var second = await GetAsync("/employee/find?tangerinoId=99");

        first.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertSpringError(await ReadAsync(first), 404, "/employee/find");
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Find_without_any_key_is_a_400()
    {
        using var response = await GetAsync("/employee/find");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400, "/employee/find");
    }

    [Fact]
    public async Task Find_returns_fired_employees_unless_ignoreFired_is_true()
    {
        await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1", "PEDIDO_DEMISSAO_COLABORADOR"));

        var found = await GetJsonAsync("/employee/find?externalId=E-1");
        using var ignored = await GetAsync("/employee/find?externalId=E-1&ignoreFired=true");

        found["fired"]!.GetValue<bool>().Should().BeTrue();
        found["resignationDate"]!.GetValue<long>().Should().Be(Payloads.Jan2024 + (30 * Payloads.Day));
        found["motivoDemissao"]!.GetValue<string>().Should().Be("PEDIDO_DEMISSAO_COLABORADOR");
        ignored.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Find_all_hides_fired_employees_unless_showFired_is_set()
    {
        await CreateEmployeeAsync("E-1");
        await CreateEmployeeAsync("E-2");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        var active = await GetJsonAsync("/employee/find-all");
        var all = await GetJsonAsync("/employee/find-all?showFired=1");
        var zero = await GetJsonAsync("/employee/find-all?showFired=0");

        active["content"]!.AsArray().Select(e => (string?)e!["externalId"]).Should().Equal("E-2");
        all["content"]!.AsArray().Select(e => (string?)e!["externalId"]).Should().Equal("E-1", "E-2");
        zero["totalElements"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Find_all_filters_by_punch_rule_branch_and_last_update()
    {
        await PostJsonAsync("/companies", new JsonObject { ["cnpj"] = "11222333000181", ["fantasyName"] = "FILIAL X", ["externalId"] = "C-X" });
        await CreateEmployeeAsync("E-1", null, e => e["punchRuleExternalId"] = "REGRA-ESTAGIO");
        await CreateEmployeeAsync("E-2", null, e => e["companyExternalId"] = "C-X");

        var byRule = await GetJsonAsync("/employee/find-all?fkPunchRule=2002");
        var byBranch = await GetJsonAsync("/employee/find-all?branchExternalId=C-X");
        var future = await GetJsonAsync($"/employee/find-all?lastUpdate={DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()}");
        var past = await GetJsonAsync("/employee/find-all?lastUpdate=0");

        byRule["content"]!.AsArray().Select(e => (string?)e!["externalId"]).Should().Equal("E-1");
        byBranch["content"]!.AsArray().Select(e => (string?)e!["externalId"]).Should().Equal("E-2");
        future["totalElements"]!.GetValue<int>().Should().Be(0);
        past["totalElements"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Dismiss_marks_the_employee_as_fired_with_date_and_reason()
    {
        var id = await CreateEmployeeAsync("E-1");

        var response = await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1", "SEM_JUSTA_CAUSA"));

        response["statusCode"]!.GetValue<string>().Should().Be("OK");
        response["statusCodeValue"]!.GetValue<int>().Should().Be(200);
        response["body"]!["id"]!.GetValue<long>().Should().Be(id);
        response["body"]!["fired"]!.GetValue<bool>().Should().BeTrue();
        var employee = (await StateAsync()).Employees.Single();
        employee.Fired.Should().BeTrue();
        employee.ResignationDate.Should().Be(Payloads.Jan2024 + (30 * Payloads.Day));
        employee.ResignationReason.Should().Be("SEM_JUSTA_CAUSA");
    }

    [Fact]
    public async Task Dismiss_by_tangerino_id_works()
    {
        var id = await CreateEmployeeAsync("E-1");

        var response = await PostJsonAsync("/employee/dismiss", new JsonObject { ["tangerinoId"] = id, ["resignationDate"] = Payloads.Jan2024 });

        response["body"]!["fired"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Dismiss_of_an_unknown_employee_is_not_found()
    {
        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("NAO-EXISTE")), "not_found", 404);
    }

    [Fact]
    public async Task Dismiss_of_an_already_fired_employee_is_already_fired()
    {
        await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1")), "already_fired", 409);
    }

    [Fact]
    public async Task Dismiss_validates_the_request()
    {
        await CreateEmployeeAsync("E-1");

        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", new JsonObject { ["resignationDate"] = Payloads.Jan2024 }), "required_field", 400);
        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", new JsonObject { ["externalId"] = "E-1" }), "required_field", 400);
        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1", "PORQUE_SIM")), "invalid_value", 400);
        var withUnknown = Payloads.Dismiss("E-1");
        withUnknown["motivo"] = "x";
        AssertEnvelopeError(await PostJsonAsync("/employee/dismiss", withUnknown), "unknown_field", 400);
        (await StateAsync()).Employees.Single().Fired.Should().BeFalse();
    }

    [Fact]
    public async Task Dismiss_prefers_the_active_employee_when_the_external_id_is_shared()
    {
        await SetBehaviorAsync(b => b.RegisterFiredExternalId = RegisterFiredExternalIdMode.CreateNew);
        var oldId = await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));
        var newId = await CreateEmployeeAsync("E-1");

        var response = await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        response["body"]!["id"]!.GetValue<long>().Should().Be(newId).And.NotBe(oldId);
    }
}
