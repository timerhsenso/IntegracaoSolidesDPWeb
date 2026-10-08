namespace SolidesDP.Fake.Tests;

public sealed class AdjustmentTests : FakeTestBase
{
    private const long Start = Payloads.Jan2024;
    private const long End = Payloads.Jan2024 + (9 * Payloads.Day);

    [Fact]
    public async Task The_documented_vacation_example_is_accepted_by_employee_external_id()
    {
        var employeeId = await CreateEmployeeAsync("E-1");

        var response = await RegisterVacationAsync("E-1", Start, End);

        response["registered"]!.GetValue<bool>().Should().BeTrue();
        response["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        response["adjustmentReasonDTO"]!["description"]!.GetValue<string>().Should().Be("FÉRIAS");
        var entity = response["entity"]!;
        entity["id"]!.GetValue<long>().Should().Be(10000);
        entity["employeeDTO"]!["id"]!.GetValue<long>().Should().Be(employeeId);
        entity["startDate"]!.GetValue<long>().Should().Be(Start);
        entity["endDate"]!.GetValue<long>().Should().Be(End);
        entity["status"]!.GetValue<string>().Should().Be("APROVADO");
        entity["origem"]!.GetValue<string>().Should().Be("Integração");
    }

    [Fact]
    public async Task The_employee_can_be_referenced_by_id_and_observation_is_stored()
    {
        var employeeId = await CreateEmployeeAsync("E-1");
        var body = Payloads.Vacation("ignored", Start, End);
        body["employeeDTO"] = new JsonObject { ["id"] = employeeId };
        body["observation"] = "ferias coletivas";

        var response = await PostJsonAsync("/adjustment/register", body);

        response["entity"]!["observation"]!.GetValue<string>().Should().Be("ferias coletivas");
        var adjustment = (await StateAsync()).Adjustments.Single();
        adjustment.EmployeeId.Should().Be(employeeId);
        adjustment.Observation.Should().Be("ferias coletivas");
        adjustment.Origem.Should().Be("Integração");
        adjustment.Excluded.Should().BeFalse();
    }

    [Fact]
    public async Task Register_1_1_accepts_flat_ids_and_defaults_status_and_full_day()
    {
        var employeeId = await CreateEmployeeAsync("E-1");

        var byExternal = await PostJsonAsync("/adjustment/register/1.1", Payloads.Vacation11("E-1", Start, End));
        var byId = await PostJsonAsync("/adjustment/register/1.1", new JsonObject
        {
            ["adjustmentReasonId"] = 5,
            ["employeeId"] = employeeId,
            ["startDate"] = End + Payloads.Day,
            ["endDate"] = End + (2 * Payloads.Day),
        });

        byExternal["registered"]!.GetValue<bool>().Should().BeTrue();
        byId["registered"]!.GetValue<bool>().Should().BeTrue();
        byId["entity"]!["status"]!.GetValue<string>().Should().Be("APROVADO");
        byId["entity"]!["fullDay"]!.GetValue<bool>().Should().BeTrue(); // herdado do motivo ATESTADO MÉDICO
    }

    [Fact]
    public async Task Unknown_or_missing_references_are_rejected_as_launch_errors()
    {
        await CreateEmployeeAsync("E-1");

        AssertLaunchError(await RegisterVacationAsync("NAO-EXISTE", Start, End));

        var unknownReason = Payloads.Vacation("E-1", Start, End);
        unknownReason["adjustmentReasonDTO"] = new JsonObject { ["id"] = 999 };
        AssertLaunchError(await PostJsonAsync("/adjustment/register", unknownReason));

        var noEmployee = Payloads.Vacation("E-1", Start, End);
        noEmployee.Remove("employeeDTO");
        AssertLaunchError(await PostJsonAsync("/adjustment/register", noEmployee));

        var noReason = Payloads.Vacation("E-1", Start, End);
        noReason.Remove("adjustmentReasonDTO");
        AssertLaunchError(await PostJsonAsync("/adjustment/register", noReason));

        var noDates = Payloads.Vacation("E-1", Start, End);
        noDates.Remove("startDate");
        AssertLaunchError(await PostJsonAsync("/adjustment/register", noDates));
        (await StateAsync()).Adjustments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fired_employee_cannot_receive_adjustments()
    {
        await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        var response = await RegisterVacationAsync("E-1", Start, End);

        AssertLaunchError(response);
        response["message"]!.GetValue<string>().Should().Contain("dismissed");
    }

    [Fact]
    public async Task Start_after_end_is_rejected_and_the_status_enum_is_validated()
    {
        await CreateEmployeeAsync("E-1");

        AssertLaunchError(await RegisterVacationAsync("E-1", End, Start));

        var badStatus = Payloads.Vacation("E-1", Start, End);
        badStatus["status"] = "APROVADA";
        AssertLaunchError(await PostJsonAsync("/adjustment/register", badStatus));

        var unknownField = Payloads.Vacation("E-1", Start, End);
        unknownField["motivo"] = "x";
        AssertLaunchError(await PostJsonAsync("/adjustment/register", unknownField));

        var sameDay = await RegisterVacationAsync("E-1", Start, Start);
        sameDay["registered"]!.GetValue<bool>().Should().BeTrue(); // start == end e valido
    }

    [Fact]
    public async Task Overlapping_adjustments_of_the_same_employee_are_rejected()
    {
        await CreateEmployeeAsync("E-1");
        await CreateEmployeeAsync("E-2");
        await RegisterVacationAsync("E-1", Start, End);

        AssertLaunchError(await RegisterVacationAsync("E-1", Start, End)); // identico (retry!)
        AssertLaunchError(await RegisterVacationAsync("E-1", End - Payloads.Day, End + (3 * Payloads.Day))); // sobrepoe a cauda
        AssertLaunchError(await RegisterVacationAsync("E-1", End, End)); // toca na ultima data (inclusivo)
        (await RegisterVacationAsync("E-1", End + Payloads.Day, End + (5 * Payloads.Day)))["registered"]!.GetValue<bool>().Should().BeTrue(); // adjacente
        (await RegisterVacationAsync("E-2", Start, End))["registered"]!.GetValue<bool>().Should().BeTrue(); // outro colaborador
        (await StateAsync()).Adjustments.Should().HaveCount(3);
    }

    [Fact]
    public async Task Overlap_check_can_be_disabled()
    {
        await SetBehaviorAsync(b => b.RejectOverlappingAdjustments = false);
        await CreateEmployeeAsync("E-1");
        await RegisterVacationAsync("E-1", Start, End);

        (await RegisterVacationAsync("E-1", Start, End))["registered"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Find_all_filters_by_employee_reason_workplace_and_ignoreExcluded()
    {
        await PostJsonAsync("/workplace/register", new JsonObject { ["name"] = "LOJA", ["externalId"] = "W-1" });
        var e1 = await CreateEmployeeAsync("E-1", null, e => e["workplaceExternalId"] = "W-1");
        var e2 = await CreateEmployeeAsync("E-2");
        var vacation = await RegisterVacationAsync("E-1", Start, End);
        var other = Payloads.Vacation("E-1", End + Payloads.Day, End + (2 * Payloads.Day));
        other["adjustmentReasonDTO"] = new JsonObject { ["id"] = 6 };
        await PostJsonAsync("/adjustment/register", other);
        await RegisterVacationAsync("E-2", Start, End);
        await PutJsonAsync($"/adjustment/update/{(long)vacation["entity"]!["id"]!}", new JsonObject { ["excluded"] = true });

        (await GetJsonAsync("/adjustment/find-all"))["totalElements"]!.GetValue<int>().Should().Be(3);
        (await GetJsonAsync("/adjustment/find-all?ignoreExcluded=true"))["totalElements"]!.GetValue<int>().Should().Be(2);
        (await GetJsonAsync("/adjustment/find-all?ignoreExcluded=false"))["totalElements"]!.GetValue<int>().Should().Be(3);
        (await GetJsonAsync($"/adjustment/find-all?employeeId={e2}"))["totalElements"]!.GetValue<int>().Should().Be(1);
        (await GetJsonAsync("/adjustment/find-all?adjustmentReasonId=6"))["totalElements"]!.GetValue<int>().Should().Be(1);
        (await GetJsonAsync($"/adjustment/find-all?employeeId={e1}&ignoreExcluded=true"))["totalElements"]!.GetValue<int>().Should().Be(1);
        var workplaceId = (long)(await GetJsonAsync("/workplace/find?externalId=W-1"))["id"]!;
        (await GetJsonAsync($"/adjustment/find-all?workplaceId={workplaceId}"))["totalElements"]!.GetValue<int>().Should().Be(2);
        (await GetJsonAsync($"/adjustment/find-all?lastUpdate={DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()}"))["totalElements"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Get_by_id_and_unknown_id()
    {
        await CreateEmployeeAsync("E-1");
        var created = await RegisterVacationAsync("E-1", Start, End);
        var id = (long)created["entity"]!["id"]!;

        var found = await GetJsonAsync($"/adjustment/{id}");
        using var missing = await GetAsync("/adjustment/424242");
        using var notANumber = await GetAsync("/adjustment/abc");

        found["id"]!.GetValue<long>().Should().Be(id);
        found["adjustmentReasonDTO"]!["id"]!.GetValue<long>().Should().Be(1);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertSpringError(await ReadAsync(missing), 404, "/adjustment/424242");
        notANumber.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_v2_with_excluded_hides_the_adjustment_and_frees_the_period()
    {
        await CreateEmployeeAsync("E-1");
        var id = (long)(await RegisterVacationAsync("E-1", Start, End))["entity"]!["id"]!;

        var response = await PutJsonAsync($"/adjustment/update/{id}", new JsonObject { ["excluded"] = true, ["adjustmenteReasonRecordId"] = id });

        response["registered"]!.GetValue<bool>().Should().BeTrue();
        response["entity"]!["id"]!.GetValue<long>().Should().Be(id);
        (await StateAsync()).Adjustments.Single().Excluded.Should().BeTrue();
        (await GetJsonAsync("/adjustment/find-all?ignoreExcluded=true"))["totalElements"]!.GetValue<int>().Should().Be(0);
        (await RegisterVacationAsync("E-1", Start, End))["registered"]!.GetValue<bool>().Should().BeTrue(); // periodo liberado

        // desfazer a exclusao agora conflita com o novo lancamento
        AssertLaunchError(await PutJsonAsync($"/adjustment/update/{id}", new JsonObject { ["excluded"] = false }));
    }

    [Fact]
    public async Task Update_v2_changes_fields_and_validates_them()
    {
        await CreateEmployeeAsync("E-1");
        var first = (long)(await RegisterVacationAsync("E-1", Start, End))["entity"]!["id"]!;
        var second = (long)(await RegisterVacationAsync("E-1", End + (5 * Payloads.Day), End + (8 * Payloads.Day)))["entity"]!["id"]!;

        var changed = await PutJsonAsync($"/adjustment/update/{first}", new JsonObject
        {
            ["adjustmentReasonId"] = 5,
            ["observation"] = "convertido em atestado",
            ["status"] = "PENDENTE",
            ["endDate"] = End + Payloads.Day,
        });

        changed["registered"]!.GetValue<bool>().Should().BeTrue();
        changed["adjustmentReasonDTO"]!["id"]!.GetValue<long>().Should().Be(5);
        changed["entity"]!["status"]!.GetValue<string>().Should().Be("PENDENTE");
        changed["entity"]!["endDate"]!.GetValue<long>().Should().Be(End + Payloads.Day);
        changed["entity"]!["observation"]!.GetValue<string>().Should().Be("convertido em atestado");
        changed["entity"]!["startDate"]!.GetValue<long>().Should().Be(Start); // omitido: mantido

        AssertLaunchError(await PutJsonAsync($"/adjustment/update/{first}", new JsonObject { ["endDate"] = End + (6 * Payloads.Day) })); // invade o segundo
        AssertLaunchError(await PutJsonAsync($"/adjustment/update/{first}", new JsonObject { ["startDate"] = End + (30 * Payloads.Day) })); // inicio > fim
        AssertLaunchError(await PutJsonAsync($"/adjustment/update/{first}", new JsonObject { ["adjustmentReasonId"] = 999 }));
        AssertLaunchError(await PutJsonAsync($"/adjustment/update/{first}", new JsonObject { ["adjustmenteReasonRecordId"] = second }));
        AssertLaunchError(await PutJsonAsync("/adjustment/update/424242", new JsonObject { ["observation"] = "x" }));
    }

    [Fact]
    public async Task Put_adjustment_updates_dates_status_and_origem_through_a_response_entity()
    {
        await CreateEmployeeAsync("E-1");
        var id = (long)(await RegisterVacationAsync("E-1", Start, End))["entity"]!["id"]!;

        var response = await PutJsonAsync($"/adjustment/{id}", new JsonObject { ["status"] = "REPROVADO", ["origem"] = "Manual", ["endDate"] = End + Payloads.Day, ["fullDay"] = false });

        response["statusCode"]!.GetValue<string>().Should().Be("OK");
        response["body"]!["status"]!.GetValue<string>().Should().Be("REPROVADO");
        var adjustment = (await StateAsync()).Adjustments.Single();
        adjustment.Status.Should().Be("REPROVADO");
        adjustment.Origem.Should().Be("Manual");
        adjustment.EndDate.Should().Be(End + Payloads.Day);
        adjustment.FullDay.Should().BeFalse();
        AssertEnvelopeError(await PutJsonAsync("/adjustment/424242", new JsonObject { ["status"] = "APROVADO" }), "not_found", 404);
        AssertEnvelopeError(await PutJsonAsync($"/adjustment/{id}", new JsonObject { ["status"] = "TALVEZ" }), "invalid_value", 400);
    }

    [Fact]
    public async Task Adjustment_reasons_are_listed()
    {
        var page = await GetJsonAsync("/adjustment-reason/find-all");

        page["content"]!.AsArray().Select(r => (long)r!["id"]!).Should().Equal(1, 4, 5, 6);
        page["content"]![0]!["description"]!.GetValue<string>().Should().Be("FÉRIAS");
        page["content"]![0]!["allowance"]!.GetValue<bool>().Should().BeTrue();
        page["content"]![0]!["fullDay"]!.GetValue<bool>().Should().BeTrue();
        page["content"]![0]!["countAsMissing"]!.GetValue<bool>().Should().BeFalse();
    }
}
