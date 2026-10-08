namespace SolidesDP.Fake.Tests;

public sealed class EmployeeRegisterTests : FakeTestBase
{
    [Fact]
    public async Task Create_returns_a_201_envelope_with_a_pin_and_ids_that_start_at_10000_and_increment()
    {
        var first = await RegisterEmployeeAsync(Payloads.Employee("E-1"));
        var second = await RegisterEmployeeAsync(Payloads.Employee("E-2"));

        first["statusCode"]!.GetValue<string>().Should().Be("CREATED");
        first["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        first["body"]!["id"]!.GetValue<long>().Should().Be(10000);
        first["body"]!["pin"]!.GetValue<string>().Should().MatchRegex("^[0-9]{6}$");
        first["body"]!["externalId"]!.GetValue<string>().Should().Be("E-1");
        first["body"]!["fired"]!.GetValue<bool>().Should().BeFalse();
        second["body"]!["id"]!.GetValue<long>().Should().Be(10001);
    }

    [Fact]
    public async Task Defaults_to_the_standard_work_schedule_and_punch_rule()
    {
        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1"));

        response["body"]!["currentWorkSchedule"]!["id"]!.GetValue<long>().Should().Be(1001);
        var employee = (await StateAsync()).Employees.Single();
        employee.WorkScheduleId.Should().Be(1001);
        employee.PunchRuleId.Should().Be(2001);
        employee.WorkScheduleHistory.Should().ContainSingle().Which.DateInMillis.Should().Be(Payloads.Jan2024);
    }

    [Fact]
    public async Task References_are_resolved_by_id_or_external_id()
    {
        var jobRoleId = (long)(await PostJsonAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1" }))["id"]!;
        var workplaceId = (long)(await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA", ["externalId"] = "W-1" }))["id"]!;
        await PostJsonAsync("/companies", new JsonObject { ["cnpj"] = "11222333000181", ["fantasyName"] = "FILIAL X", ["externalId"] = "C-X" });

        var byId = Payloads.Employee("E-ID");
        byId["jobRole"] = jobRoleId;
        byId["workplace"] = workplaceId;
        byId["company"] = 3002;
        byId["workSchedule"] = 1002;
        var byExternal = Payloads.Employee("E-EXT");
        byExternal["jobRoleExternalId"] = "J-1";
        byExternal["workplaceExternalId"] = "W-1";
        byExternal["companyExternalId"] = "C-X";
        byExternal["workScheduleExternalId"] = "ESC-12X36";
        byExternal["punchRuleExternalId"] = "REGRA-ESTAGIO";

        var a = (await RegisterEmployeeAsync(byId))["body"]!;
        var b = (await RegisterEmployeeAsync(byExternal))["body"]!;

        a["jobRoleDTO"]!["id"]!.GetValue<long>().Should().Be(jobRoleId);
        a["currentWorkplaceDTO"]!["id"]!.GetValue<long>().Should().Be(workplaceId);
        a["company"]!["id"]!.GetValue<long>().Should().Be(3002);
        a["currentWorkSchedule"]!["id"]!.GetValue<long>().Should().Be(1002);
        b["jobRoleDTO"]!["externalId"]!.GetValue<string>().Should().Be("J-1");
        b["currentWorkplaceDTO"]!["externalId"]!.GetValue<string>().Should().Be("W-1");
        b["company"]!["externalId"]!.GetValue<string>().Should().Be("C-X");
        b["currentWorkSchedule"]!["externalId"]!.GetValue<string>().Should().Be("ESC-12X36");
        (await StateAsync()).Employees.Last().PunchRuleId.Should().Be(2002);
    }

    [Theory]
    [InlineData("jobRole", 999)]
    [InlineData("jobRoleExternalId", "NAO-EXISTE")]
    [InlineData("workplace", 999)]
    [InlineData("workplaceExternalId", "NAO-EXISTE")]
    [InlineData("company", 1)]
    [InlineData("companyExternalId", "NAO-EXISTE")]
    [InlineData("workSchedule", 5)]
    [InlineData("workScheduleExternalId", "NAO-EXISTE")]
    [InlineData("punchRuleExternalId", "NAO-EXISTE")]
    public async Task Unknown_references_are_rejected_and_nothing_is_persisted(string field, object value)
    {
        var payload = Payloads.Employee("E-1");
        payload[field] = JsonValue.Create(value);

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "invalid_reference", 400);
        (await StateAsync()).Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task Existing_external_id_without_allowUpdate_is_already_exists()
    {
        await CreateEmployeeAsync("E-1");

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1"));

        AssertEnvelopeError(response, "already_exists", 409);
        (await StateAsync()).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task AllowUpdate_updates_the_same_employee_and_answers_200_OK()
    {
        var id = await CreateEmployeeAsync("E-1");
        var update = Payloads.Employee("E-1");
        update["name"] = "NOME NOVO";

        var response = await RegisterEmployeeAsync(update, allowUpdate: true);

        response["statusCode"]!.GetValue<string>().Should().Be("OK");
        response["statusCodeValue"]!.GetValue<int>().Should().Be(200);
        response["body"]!["id"]!.GetValue<long>().Should().Be(id);
        response["body"]!["name"]!.GetValue<string>().Should().Be("NOME NOVO");
        (await StateAsync()).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task AllowUpdate_can_target_an_employee_by_tangerinoId()
    {
        var id = await CreateEmployeeAsync("E-1");
        var update = Payloads.Employee("E-RENAMED");
        update["tangerinoId"] = id;

        var response = await RegisterEmployeeAsync(update, allowUpdate: true);

        response["body"]!["id"]!.GetValue<long>().Should().Be(id);
        response["body"]!["externalId"]!.GetValue<string>().Should().Be("E-RENAMED");
        var unknown = Payloads.Employee("E-X");
        unknown["tangerinoId"] = 424242;
        AssertEnvelopeError(await RegisterEmployeeAsync(unknown, allowUpdate: true), "not_found", 404);
    }

    [Fact]
    public async Task Work_schedule_history_grows_only_when_schedule_or_date_changes()
    {
        await CreateEmployeeAsync("E-1");

        await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true); // igual: nao acrescenta
        var changedSchedule = Payloads.Employee("E-1");
        changedSchedule["workScheduleExternalId"] = "ESC-12X36";
        changedSchedule["workScheduleDateInMillis"] = Payloads.Jan2024 + (31 * Payloads.Day);
        var response = await RegisterEmployeeAsync(changedSchedule, allowUpdate: true);
        await RegisterEmployeeAsync(changedSchedule, allowUpdate: true); // repetir o mesmo: nao acrescenta
        var changedDateOnly = Payloads.Employee("E-1");
        changedDateOnly["workScheduleExternalId"] = "ESC-12X36";
        changedDateOnly["workScheduleDateInMillis"] = Payloads.Jan2024 + (60 * Payloads.Day);
        await RegisterEmployeeAsync(changedDateOnly, allowUpdate: true);

        var history = (await StateAsync()).Employees.Single().WorkScheduleHistory;
        history.Select(h => (h.WorkScheduleId, h.DateInMillis)).Should().Equal(
            (1001L, Payloads.Jan2024),
            (1002L, Payloads.Jan2024 + (31 * Payloads.Day)),
            (1002L, Payloads.Jan2024 + (60 * Payloads.Day)));
        response["body"]!["currentWorkSchedule"]!["id"]!.GetValue<long>().Should().Be(1002);
        response["body"]!["workScheduleList"]!.AsArray().Should().HaveCount(2);
        response["body"]!["workScheduleList"]![1]!["alterationDate"]!.GetValue<long>().Should().Be(Payloads.Jan2024 + (31 * Payloads.Day));
    }

    [Fact]
    public async Task Update_with_Keep_keeps_properties_omitted_from_the_request()
    {
        await CreateEmployeeAsync("E-1", "12345678901", e =>
        {
            e["email"] = "a@b.com";
            e["matricula"] = "M-1";
            e["jobRoleExternalId"] = null;
        });
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA", ["externalId"] = "W-1" });
        var withWorkplace = Payloads.Employee("E-1");
        withWorkplace["workplaceExternalId"] = "W-1";
        await RegisterEmployeeAsync(withWorkplace, allowUpdate: true);

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true);

        var body = response["body"]!;
        body["cpf"]!.GetValue<string>().Should().Be("12345678901");
        body["email"]!.GetValue<string>().Should().Be("a@b.com");
        body["currentWorkplaceDTO"]!["externalId"]!.GetValue<string>().Should().Be("W-1");
        ((string?)(await StateAsync()).Employees.Single().Fields["matricula"]).Should().Be("M-1");
    }

    [Fact]
    public async Task Update_with_Clear_clears_properties_omitted_from_the_request()
    {
        await SetBehaviorAsync(b => b.UpdateOmittedFields = UpdateOmittedFieldsMode.Clear);
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA", ["externalId"] = "W-1" });
        await CreateEmployeeAsync("E-1", "12345678901", e =>
        {
            e["email"] = "a@b.com";
            e["matricula"] = "M-1";
            e["workplaceExternalId"] = "W-1";
        });

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true);

        var body = response["body"]!;
        body["cpf"].Should().BeNull();
        body["email"].Should().BeNull();
        body["currentWorkplaceDTO"].Should().BeNull();
        (await StateAsync()).Employees.Single().Fields.ContainsKey("matricula").Should().BeFalse();
        body["currentWorkSchedule"]!["id"]!.GetValue<long>().Should().Be(1001); // sem escala informada: padrao
    }

    [Fact]
    public async Task Null_values_are_treated_like_omitted_ones()
    {
        await CreateEmployeeAsync("E-1", "12345678901", e => e["email"] = "a@b.com");
        var update = Payloads.Employee("E-1");
        update["email"] = null;

        var kept = await RegisterEmployeeAsync(update, allowUpdate: true);
        await SetBehaviorAsync(b => b.UpdateOmittedFields = UpdateOmittedFieldsMode.Clear);
        var cleared = await RegisterEmployeeAsync(update, allowUpdate: true);

        kept["body"]!["email"]!.GetValue<string>().Should().Be("a@b.com");
        cleared["body"]!["email"].Should().BeNull();
    }

    [Fact]
    public async Task Fired_external_id_is_an_error_by_default()
    {
        await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        AssertEnvelopeError(await RegisterEmployeeAsync(Payloads.Employee("E-1")), "employee_fired", 409);
        AssertEnvelopeError(await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true), "employee_fired", 409);
        (await StateAsync()).Employees.Should().ContainSingle().Which.Fired.Should().BeTrue();
    }

    [Fact]
    public async Task Fired_external_id_with_CreateNew_creates_another_employee_and_keeps_the_old_one_fired()
    {
        await SetBehaviorAsync(b => b.RegisterFiredExternalId = RegisterFiredExternalIdMode.CreateNew);
        var oldId = await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1"), allowUpdate: true);

        response["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        response["body"]!["id"]!.GetValue<long>().Should().NotBe(oldId);
        var employees = (await StateAsync()).Employees;
        employees.Should().HaveCount(2);
        employees.Single(e => e.Id == oldId).Fired.Should().BeTrue();
        employees.Single(e => e.Id != oldId).Fired.Should().BeFalse();
        (await GetJsonAsync("/employee/find?externalId=E-1"))["id"]!.GetValue<long>().Should().NotBe(oldId); // o ativo tem prioridade
    }

    [Fact]
    public async Task Fired_external_id_with_Reactivate_brings_the_same_employee_back()
    {
        await SetBehaviorAsync(b => b.RegisterFiredExternalId = RegisterFiredExternalIdMode.Reactivate);
        var id = await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));
        var again = Payloads.Employee("E-1");
        again["name"] = "VOLTOU";

        var response = await RegisterEmployeeAsync(again);

        response["statusCodeValue"]!.GetValue<int>().Should().Be(200);
        response["body"]!["id"]!.GetValue<long>().Should().Be(id);
        response["body"]!["fired"]!.GetValue<bool>().Should().BeFalse();
        response["body"]!["resignationDate"].Should().BeNull();
        (await StateAsync()).Employees.Should().ContainSingle().Which.Name.Should().Be("VOLTOU");
    }

    [Fact]
    public async Task Cpf_of_another_active_employee_is_a_duplicate_cpf()
    {
        await CreateEmployeeAsync("E-1", "11111111111");

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-2", "11111111111"));

        AssertEnvelopeError(response, "duplicate_cpf", 409);
        (await StateAsync()).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task Double_bind_allows_a_repeated_cpf()
    {
        await CreateEmployeeAsync("E-1", "11111111111");
        var second = Payloads.Employee("E-2", "11111111111");
        second["doubleBindEmployee"] = true;

        var response = await RegisterEmployeeAsync(second);

        response["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        response["body"]!["doubleBindEmployee"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Cpf_of_a_fired_employee_can_be_reused_and_the_uniqueness_check_can_be_disabled()
    {
        await CreateEmployeeAsync("E-1", "11111111111");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));
        (await RegisterEmployeeAsync(Payloads.Employee("E-2", "11111111111")))["statusCodeValue"]!.GetValue<int>().Should().Be(201);

        AssertEnvelopeError(await RegisterEmployeeAsync(Payloads.Employee("E-3", "11111111111")), "duplicate_cpf", 409);
        await SetBehaviorAsync(b => b.UniqueCpfAmongActive = false);
        (await RegisterEmployeeAsync(Payloads.Employee("E-3", "11111111111")))["statusCodeValue"]!.GetValue<int>().Should().Be(201);
    }

    [Fact]
    public async Task Updating_to_the_cpf_of_another_active_employee_is_rejected_but_keeping_your_own_is_fine()
    {
        await CreateEmployeeAsync("E-1", "11111111111");
        await CreateEmployeeAsync("E-2", "22222222222");

        var sameCpf = await RegisterEmployeeAsync(Payloads.Employee("E-2", "22222222222"), allowUpdate: true);
        var stolenCpf = await RegisterEmployeeAsync(Payloads.Employee("E-2", "11111111111"), allowUpdate: true);

        sameCpf["statusCodeValue"]!.GetValue<int>().Should().Be(200);
        AssertEnvelopeError(stolenCpf, "duplicate_cpf", 409);
    }

    [Fact]
    public async Task The_gestorId_header_and_extra_dto_properties_are_stored()
    {
        var payload = Payloads.Employee("E-1");
        payload["matricula"] = "M-77";
        payload["employeeSalary"] = new JsonObject { ["salaryType"] = "MES", ["salaryValue"] = 3500.5, ["moeda"] = "BRL" };
        using var content = JsonBody(payload);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Rel("/employee/register?skipUnifiedSync=true")) { Content = content };
        request.Headers.Add("gestorId", "77");

        using var response = await Host.Api.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var employee = (await StateAsync()).Employees.Single();
        employee.LinkedManagerId.Should().Be(77);
        ((string?)employee.Fields["matricula"]).Should().Be("M-77");
        ((string?)employee.Fields["employeeSalary"]!["salaryType"]).Should().Be("MES");
    }
}
