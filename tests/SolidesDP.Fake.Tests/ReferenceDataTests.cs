namespace SolidesDP.Fake.Tests;

public sealed class SeedTests : FakeTestBase
{
    private static readonly string[] ClientCnpjs =
    [
        "00594807000108", "00594807000361", "04222142000235", "04222142000316", "05896710000165", "06916109000150", "07052354000129",
        "63356000000149", "07245648000177", "10902623000103", "22091954000190", "26728866000107", "10902623000294", "21488699000150",
    ];

    [Fact]
    public async Task Companies_are_seeded_with_the_clients_cnpjs_from_id_3001()
    {
        var page = await GetJsonAsync("/companies");

        var companies = page["content"]!.AsArray();
        companies.Select(c => (long)c!["id"]!).Should().Equal(Enumerable.Range(3001, 14).Select(i => (long)i));
        companies.Select(c => (string?)c!["cnpj"]).Should().Equal(ClientCnpjs);
        companies.Should().OnlyContain(c => c!["externalId"] == null && !string.IsNullOrWhiteSpace((string?)c["fantasyName"]));
        companies[0]!["cnpjMask"]!.GetValue<string>().Should().Be("00.594.807/0001-08");
    }

    [Fact]
    public async Task Work_schedules_are_seeded()
    {
        var page = await GetJsonAsync("/work-schedule");
        var defaultSchedule = await GetJsonAsync("/work-schedule/default");

        var schedules = page["content"]!.AsArray();
        schedules.Select(s => (long)s!["id"]!).Should().Equal(1001, 1002);
        schedules[0]!["name"]!.GetValue<string>().Should().Be("ESCALA PADRAO 44H SEG-SEX");
        schedules[0]!["externalId"]!.GetValue<string>().Should().Be("ESC-PADRAO");
        schedules[0]!["standard"]!.GetValue<bool>().Should().BeTrue();
        schedules[0]!["workScheduleTimetableList"]!.AsArray().Should().HaveCount(7);
        schedules[1]!["name"]!.GetValue<string>().Should().Be("ESCALA 12X36");
        schedules[1]!["externalId"]!.GetValue<string>().Should().Be("ESC-12X36");
        schedules[1]!["standard"]!.GetValue<bool>().Should().BeFalse();
        defaultSchedule["id"]!.GetValue<long>().Should().Be(1001);
        (await GetJsonAsync("/work-schedule?active=false"))["totalElements"]!.GetValue<int>().Should().Be(0);
        (await GetJsonAsync("/work-schedule?active=true"))["totalElements"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Punch_rules_are_seeded_inside_the_base_item_envelope()
    {
        var response = await GetJsonAsync("/v2/punch-rule");

        response["code"]!.GetValue<int>().Should().Be(200);
        response["status"]!.GetValue<string>().Should().Be("OK");
        response["messages"]!.AsArray().Should().BeEmpty();
        var rules = response["item"]!["content"]!.AsArray();
        rules.Select(r => ((long)r!["id"]!, (string?)r["description"], (string?)r["externalId"], (bool)r["standard"]!)).Should().Equal(
            (2001L, "REGRA PADRAO", "REGRA-PADRAO", true),
            (2002L, "REGRA ESTAGIO", "REGRA-ESTAGIO", false));
    }

    [Fact]
    public async Task Job_roles_workplaces_employees_and_adjustments_start_empty()
    {
        var state = await StateAsync();

        state.JobRoles.Should().BeEmpty();
        state.Workplaces.Should().BeEmpty();
        state.Employees.Should().BeEmpty();
        state.Adjustments.Should().BeEmpty();
        state.AdjustmentReasons.Select(r => r.Id).Should().Equal(1, 4, 5, 6);
        state.AdjustmentReasons.Select(r => r.Description).Should().Equal("FÉRIAS", "ABONO", "ATESTADO MÉDICO", "FOLGA");
    }
}

public sealed class CompanyTests : FakeTestBase
{
    [Fact]
    public async Task Create_company_generates_an_id_from_10000_and_a_mask()
    {
        var created = await PostJsonAsync("/companies", new JsonObject { ["cnpj"] = "11222333000181", ["fantasyName"] = "FILIAL NOVA", ["socialReason"] = "NOVA LTDA", ["externalId"] = "F-1" });

        created["id"]!.GetValue<long>().Should().Be(10000);
        created["cnpj"]!.GetValue<string>().Should().Be("11222333000181");
        created["cnpjMask"]!.GetValue<string>().Should().Be("11.222.333/0001-81");
        created["externalId"]!.GetValue<string>().Should().Be("F-1");
        (await GetJsonAsync("/companies"))["totalElements"]!.GetValue<int>().Should().Be(15);
    }

    [Fact]
    public async Task Duplicate_cnpj_is_rejected_even_when_sent_with_a_mask()
    {
        using var plain = await PostAsync("/companies", new JsonObject { ["cnpj"] = "00594807000108" });
        using var masked = await PostAsync("/companies", new JsonObject { ["cnpj"] = "00.594.807/0001-08" });

        plain.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertSpringError(await ReadAsync(plain), 409, "/companies");
        masked.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("""{"fantasyName":"SEM CNPJ"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"cnpj":"123"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"cnpj":"11222333000181","inventado":1}""", HttpStatusCode.BadRequest)]
    public async Task Invalid_company_requests_are_400(string body, HttpStatusCode expected)
    {
        using var response = await PostAsync("/companies", JsonNode.Parse(body)!);

        response.StatusCode.Should().Be(expected);
        AssertSpringError(await ReadAsync(response), 400);
    }
}

public sealed class JobRoleTests : FakeTestBase
{
    [Fact]
    public async Task Register_creates_a_job_role_that_can_be_found_by_both_keys()
    {
        var created = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA DE RH", ["externalId"] = "J-1", ["cbo"] = "212405" });

        created["id"]!.GetValue<long>().Should().Be(10000);
        created["description"]!.GetValue<string>().Should().Be("ANALISTA DE RH");
        created["externalId"]!.GetValue<string>().Should().Be("J-1");
        created["cbo"]!.GetValue<string>().Should().Be("212405");
        (await GetJsonAsync("/job-role/find?externalId=J-1"))["id"]!.GetValue<long>().Should().Be(10000);
        (await GetJsonAsync("/job-role/find?tangerinoId=10000"))["externalId"]!.GetValue<string>().Should().Be("J-1");
        (await GetJsonAsync("/job-role/find-all"))["totalElements"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Description_is_required_and_external_id_and_cbo_are_optional()
    {
        using var missing = await PostAsync("/job-role/register", new JsonObject { ["externalId"] = "J-1" });
        var minimal = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "AUXILIAR" });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(missing), 400, "/job-role/register");
        minimal["externalId"].Should().BeNull();
        minimal["cbo"].Should().BeNull();
    }

    [Fact]
    public async Task Find_of_an_unknown_job_role_is_404_and_without_keys_is_400()
    {
        using var unknown = await GetAsync("/job-role/find?externalId=NAO-EXISTE");
        using var noKey = await GetAsync("/job-role/find");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_external_id_is_an_error_by_default()
    {
        await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1" });

        using var response = await PostAsync("/job-role/register", new JsonObject { ["description"] = "OUTRO", ["externalId"] = "J-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertSpringError(await ReadAsync(response), 409, "/job-role/register");
        (await StateAsync()).JobRoles.Should().ContainSingle();
    }

    [Fact]
    public async Task Duplicate_external_id_with_Duplicate_creates_another_job_role()
    {
        await SetBehaviorAsync(b => b.JobRoleDuplicateExternalId = JobRoleDuplicateExternalIdMode.Duplicate);
        await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1" });

        var second = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "OUTRO", ["externalId"] = "J-1" });

        second["id"]!.GetValue<long>().Should().Be(10001);
        (await StateAsync()).JobRoles.Select(j => j.Description).Should().Equal("ANALISTA", "OUTRO");
        (await GetJsonAsync("/job-role/find?externalId=J-1"))["id"]!.GetValue<long>().Should().Be(10000); // o primeiro
    }

    [Fact]
    public async Task Duplicate_external_id_with_Update_updates_the_existing_job_role_honoring_omitted_fields()
    {
        await SetBehaviorAsync(b => b.JobRoleDuplicateExternalId = JobRoleDuplicateExternalIdMode.Update);
        await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1", ["cbo"] = "212405" });

        var kept = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA SR", ["externalId"] = "J-1" });
        await SetBehaviorAsync(b => b.UpdateOmittedFields = UpdateOmittedFieldsMode.Clear);
        var cleared = await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA PL", ["externalId"] = "J-1" });

        kept["id"]!.GetValue<long>().Should().Be(10000);
        kept["description"]!.GetValue<string>().Should().Be("ANALISTA SR");
        kept["cbo"]!.GetValue<string>().Should().Be("212405");
        cleared["description"]!.GetValue<string>().Should().Be("ANALISTA PL");
        cleared["cbo"].Should().BeNull();
        (await StateAsync()).JobRoles.Should().ContainSingle();
    }
}

public sealed class WorkplaceTests : FakeTestBase
{
    [Fact]
    public async Task Register_find_and_list()
    {
        var created = await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA CENTRO", ["externalId"] = "W-1" });
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "SEM CODIGO" });

        created["id"]!.GetValue<long>().Should().Be(10000);
        created["name"]!.GetValue<string>().Should().Be("LOJA CENTRO");
        created["active"]!.GetValue<bool>().Should().BeTrue();
        created["standard"]!.GetValue<bool>().Should().BeFalse();
        (await GetJsonAsync("/workplace/find?externalId=W-1"))["id"]!.GetValue<long>().Should().Be(10000);
        (await GetJsonAsync("/workplace/find?tangerinoId=10001"))["name"]!.GetValue<string>().Should().Be("SEM CODIGO");
        (await GetJsonAsync("/workplace/find-all"))["totalElements"]!.GetValue<int>().Should().Be(2);
        (await GetJsonAsync("/workplace/find-all?active=false"))["totalElements"]!.GetValue<int>().Should().Be(0);
        (await GetJsonAsync("/workplace/find-all?active=true"))["totalElements"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Name_is_required()
    {
        using var response = await PostAsync("/workplace/register", new JsonObject { ["externalId"] = "W-1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400, "/workplace/register");
    }

    [Fact]
    public async Task Existing_external_id_needs_allowUpdate()
    {
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA CENTRO", ["externalId"] = "W-1" });

        using var withoutFlag = await PostAsync("/workplace/register", new JsonObject { ["name"] = "RENOMEADA", ["externalId"] = "W-1" });
        using var falseFlag = await PostAsync("/workplace/register?allowUpdate=false", new JsonObject { ["name"] = "RENOMEADA", ["externalId"] = "W-1" });
        var updated = await PostJsonAsync("/workplace/register?allowUpdate=true", new JsonObject { ["name"] = "RENOMEADA", ["externalId"] = "W-1" });

        withoutFlag.StatusCode.Should().Be(HttpStatusCode.Conflict);
        falseFlag.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertSpringError(await ReadAsync(withoutFlag), 409, "/workplace/register");
        updated["id"]!.GetValue<long>().Should().Be(10000);
        updated["name"]!.GetValue<string>().Should().Be("RENOMEADA");
        (await StateAsync()).Workplaces.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_workplace_is_404()
    {
        using var response = await GetAsync("/workplace/find?externalId=NAO-EXISTE");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
