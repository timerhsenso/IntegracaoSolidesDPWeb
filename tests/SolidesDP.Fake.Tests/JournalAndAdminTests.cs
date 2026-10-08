using System.Text;
using System.Text.Json;

namespace SolidesDP.Fake.Tests;

public sealed class JournalTests : FakeTestBase
{
    [Fact]
    public async Task Requests_are_recorded_in_order_with_method_path_query_body_and_status()
    {
        await GetAsync("/employee/find?externalId=E-1&ignoreFired=true");
        await PostAsync("/job-role/register", new JsonObject { ["description"] = "ANALISTA", ["externalId"] = "J-1" });
        await PostAsync("/employee/register?allowUpdate=true", Payloads.Employee("E-1"));

        var journal = await Host.Admin.GetRequestsAsync(Ct);

        journal.Select(r => r.Seq).Should().Equal(1, 2, 3);
        journal.Select(r => (r.Method, r.Path, r.Status)).Should().Equal(("GET", "/employee/find", 404), ("POST", "/job-role/register", 200), ("POST", "/employee/register", 200));
        journal[0].Query.Should().Be("externalId=E-1&ignoreFired=true");
        journal[0].Body.Should().BeNull();
        journal[1].Query.Should().BeNull();
        journal[1].Body!["description"]!.GetValue<string>().Should().Be("ANALISTA");
        journal[2].Query.Should().Be("allowUpdate=true");
        journal[2].Body!["externalId"]!.GetValue<string>().Should().Be("E-1");
        journal.Should().OnlyContain(r => r.At > DateTimeOffset.UtcNow.AddMinutes(-5) && r.Fault == null);
    }

    [Fact]
    public async Task Invalid_requests_and_non_json_bodies_are_recorded_as_they_arrived()
    {
        using var raw = new StringContent("{ not json", Encoding.UTF8, "application/json");
        await Host.Api.PostAsync(Rel("/employee/register"), raw, Ct);
        await SendWithAuthorizationAsync("/test", null);

        var journal = await Host.Admin.GetRequestsAsync(Ct);

        journal[0].Status.Should().Be(400);
        journal[0].Body!.GetValue<string>().Should().Be("{ not json");
        journal[1].Status.Should().Be(401); // 401 tambem e registrado
        journal[1].Path.Should().Be("/test");
    }

    [Fact]
    public async Task Admin_calls_are_not_journaled_and_the_journal_can_be_cleared()
    {
        await GetAsync("/test");
        await Host.Admin.GetStateAsync(Ct);
        await Host.Admin.GetBehaviorAsync(Ct);
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/nada", Status = 500 }, Ct);

        (await Host.Admin.GetRequestsAsync(Ct)).Should().ContainSingle().Which.Path.Should().Be("/test");

        await Host.Admin.ClearRequestsAsync(Ct);
        (await Host.Admin.GetRequestsAsync(Ct)).Should().BeEmpty();
        await GetAsync("/test");
        (await Host.Admin.GetRequestsAsync(Ct)).Should().ContainSingle().Which.Seq.Should().Be(1);
    }

    [Fact]
    public async Task Injected_faults_are_flagged_in_the_journal()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Status = 503 }, Ct);

        await GetAsync("/test");
        await GetAsync("/test");

        var journal = await Host.Admin.GetRequestsAsync(Ct);
        journal.Select(r => (r.Status, r.Fault)).Should().Equal((503, "Status"), (200, null));
    }

    [Fact]
    public async Task Reset_clears_the_journal()
    {
        await GetAsync("/test");

        await Host.Admin.ResetAsync(Ct);

        (await Host.Admin.GetRequestsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_journal_entry_is_visible_as_soon_as_the_response_arrives()
    {
        for (var i = 0; i < 25; i++)
        {
            await GetAsync("/test");
            (await Host.Admin.GetRequestsAsync(Ct)).Should().HaveCount(i + 1);
        }
    }
}

public sealed class AdminTests : FakeTestBase
{
    [Fact]
    public async Task State_exposes_every_collection_with_internal_records()
    {
        await CreateEmployeeAsync("E-1", "11111111111", e => e["matricula"] = "M-1");
        await CreateEmployeeAsync("E-2");
        await PostJsonAsync("/employee/dismiss", Payloads.Dismiss("E-2"));
        await RegisterVacationAsync("E-1", Payloads.Jan2024, Payloads.Jan2024 + Payloads.Day);

        using var document = await Host.Admin.GetStateDocumentAsync(Ct);
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("companies", "jobRoles", "workplaces", "workSchedules", "punchRules", "adjustmentReasons", "employees", "adjustments");
        var employees = root.GetProperty("employees");
        employees.GetArrayLength().Should().Be(2);
        employees[0].GetProperty("fired").GetBoolean().Should().BeFalse();
        employees[1].GetProperty("fired").GetBoolean().Should().BeTrue();
        employees[1].GetProperty("resignationReason").GetString().Should().Be("DEMISSAO_EMPRESA");
        employees[0].GetProperty("workScheduleHistory").GetArrayLength().Should().Be(1);
        employees[0].GetProperty("fields").GetProperty("matricula").GetString().Should().Be("M-1");
        root.GetProperty("adjustments")[0].GetProperty("excluded").GetBoolean().Should().BeFalse();

        var typed = await StateAsync();
        typed.Employees.Select(e => e.ExternalId).Should().Equal("E-1", "E-2");
        typed.Adjustments.Should().ContainSingle();
    }

    [Fact]
    public async Task Reset_reseeds_and_clears_everything()
    {
        await CreateEmployeeAsync("E-1");
        await PostJsonAsync("/companies", new JsonObject { ["cnpj"] = "11222333000181" });
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/nada" }, Ct);
        await SetBehaviorAsync(b => b.ErrorStyle = ErrorStyle.Http);

        await Host.Admin.ResetAsync(Ct);

        var state = await StateAsync();
        state.Employees.Should().BeEmpty();
        state.Companies.Should().HaveCount(14);
        (await Host.Admin.GetFaultsAsync(Ct)).Should().BeEmpty();
        (await Host.Admin.GetBehaviorAsync(Ct)).ErrorStyle.Should().Be(ErrorStyle.ResponseEntity);
        (await CreateEmployeeAsync("E-1")).Should().Be(10000); // contadores de id recomecam
    }

    [Fact]
    public async Task Default_behavior_matches_the_documented_defaults_and_appsettings()
    {
        var behavior = await Host.Admin.GetBehaviorAsync(Ct);

        behavior.Should().BeEquivalentTo(new FakeBehavior());
        behavior.ErrorStyle.Should().Be(ErrorStyle.ResponseEntity);
        behavior.RequireBasicPrefix.Should().BeTrue();
        behavior.RejectUnknownFields.Should().BeTrue();
        behavior.UniqueCpfAmongActive.Should().BeTrue();
        behavior.UpdateOmittedFields.Should().Be(UpdateOmittedFieldsMode.Keep);
        behavior.RegisterFiredExternalId.Should().Be(RegisterFiredExternalIdMode.Error);
        behavior.JobRoleDuplicateExternalId.Should().Be(JobRoleDuplicateExternalIdMode.Error);
        behavior.RejectOverlappingAdjustments.Should().BeTrue();
    }

    [Fact]
    public async Task Behavior_is_written_as_camel_case_with_enums_as_strings_and_read_case_insensitively()
    {
        using var read = await Host.Anonymous.GetAsync(Rel("/_fake/behavior"), Ct);
        var json = await ReadAsync(read);
        json.AsObject().Select(p => p.Key).Should().BeEquivalentTo(
            "errorStyle", "requireBasicPrefix", "rejectUnknownFields", "uniqueCpfAmongActive", "updateOmittedFields", "registerFiredExternalId", "jobRoleDuplicateExternalId", "rejectOverlappingAdjustments");
        json["errorStyle"]!.GetValue<string>().Should().Be("ResponseEntity");

        using var content = new StringContent("""{"ErrorStyle":"Http","registerFiredExternalId":"Reactivate"}""", Encoding.UTF8, "application/json");
        using var put = await Host.Anonymous.PutAsync(Rel("/_fake/behavior"), content, Ct);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var behavior = await Host.Admin.GetBehaviorAsync(Ct);
        behavior.ErrorStyle.Should().Be(ErrorStyle.Http);
        behavior.RegisterFiredExternalId.Should().Be(RegisterFiredExternalIdMode.Reactivate);
        behavior.RequireBasicPrefix.Should().BeTrue(); // omitido: volta ao padrao (substituicao, nao merge)
    }

    [Theory]
    [InlineData("""{"errorStyle":"Soap"}""")]
    [InlineData("""{"errorStyl":"Http"}""")]
    [InlineData("""{"requireBasicPrefix":"maybe"}""")]
    [InlineData("")]
    public async Task Invalid_behavior_bodies_are_rejected_and_do_not_change_anything(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Host.Anonymous.PutAsync(Rel("/_fake/behavior"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400, "/_fake/behavior");
        (await Host.Admin.GetBehaviorAsync(Ct)).Should().BeEquivalentTo(new FakeBehavior());
    }

    [Fact]
    public async Task UpdateBehaviorAsync_changes_only_what_was_asked()
    {
        var updated = await Host.Admin.UpdateBehaviorAsync(b => b.UniqueCpfAmongActive = false, Ct);

        updated.UniqueCpfAmongActive.Should().BeFalse();
        (await Host.Admin.GetBehaviorAsync(Ct)).Should().BeEquivalentTo(new FakeBehavior { UniqueCpfAmongActive = false });
    }

    [Fact]
    public async Task Out_of_range_numeric_query_values_are_a_spring_400_not_a_500()
    {
        using var response = await GetAsync("/employee/find?tangerinoId=99999999999999999999999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400);
    }
}

public sealed class SeedFileTests
{
    [Fact]
    public async Task A_custom_seed_file_replaces_the_embedded_one_and_survives_reset()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fake-seed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """
            {
              "companies": [ { "id": 7, "cnpj": "11222333000181", "cnpjMask": "11.222.333/0001-81", "fantasyName": "UNICA" } ],
              "workSchedules": [ { "id": 5001, "name": "UNICA", "externalId": "U", "standard": true } ],
              "punchRules": [ { "id": 6001, "description": "UNICA", "externalId": "R", "standard": true } ],
              "adjustmentReasons": [ { "id": 9, "description": "OUTRO MOTIVO", "active": true } ],
              "jobRoles": [ { "id": 20000, "description": "JA EXISTE", "externalId": "J-0", "alterationDate": 1 } ]
            }
            """, TestContext.Current.CancellationToken);
        try
        {
            await using var host = new FakeHost(new Dictionary<string, string?> { ["Fake:SeedFile"] = file });

            var state = await host.Admin.GetStateAsync(TestContext.Current.CancellationToken);
            state.Companies.Should().ContainSingle().Which.Id.Should().Be(7);
            state.AdjustmentReasons.Should().ContainSingle().Which.Id.Should().Be(9);
            state.WorkSchedules.Single().Id.Should().Be(5001);

            using var content = new StringContent("""{"description":"NOVO"}""", Encoding.UTF8, "application/json");
            using var created = await host.Api.PostAsync(new Uri("/job-role/register", UriKind.Relative), content, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            json.RootElement.GetProperty("id").GetInt64().Should().Be(20001); // os ids novos continuam acima do maior id do seed

            await host.Admin.ResetAsync(TestContext.Current.CancellationToken);
            (await host.Admin.GetStateAsync(TestContext.Current.CancellationToken)).JobRoles.Should().ContainSingle().Which.Id.Should().Be(20000);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_invalid_seed_file_fails_at_startup()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fake-seed-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, "{ nao e json", TestContext.Current.CancellationToken);
        try
        {
            var act = () => new FakeHost(new Dictionary<string, string?> { ["Fake:SeedFile"] = file });

            act.Should().Throw<Exception>();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_missing_seed_file_fails_at_startup()
    {
        var act = () => new FakeHost(new Dictionary<string, string?> { ["Fake:SeedFile"] = "/nao/existe/seed.json" });

        act.Should().Throw<Exception>();
    }
}

public sealed class ConfiguredBehaviorTests() : FakeTestBase(new Dictionary<string, string?>
{
    ["Fake:Behavior:RejectUnknownFields"] = "false",
    ["Fake:Behavior:UpdateOmittedFields"] = "Clear",
    ["Fake:Behavior:RegisterFiredExternalId"] = "CreateNew",
    ["Fake:Behavior:JobRoleDuplicateExternalId"] = "Duplicate",
})
{
    [Fact]
    public async Task Behavior_can_be_configured_and_reset_goes_back_to_the_configured_values_not_the_code_defaults()
    {
        var configured = await Host.Admin.GetBehaviorAsync(Ct);
        configured.RejectUnknownFields.Should().BeFalse();
        configured.UpdateOmittedFields.Should().Be(UpdateOmittedFieldsMode.Clear);
        configured.RegisterFiredExternalId.Should().Be(RegisterFiredExternalIdMode.CreateNew);
        configured.JobRoleDuplicateExternalId.Should().Be(JobRoleDuplicateExternalIdMode.Duplicate);
        configured.RequireBasicPrefix.Should().BeTrue();

        await SetBehaviorAsync(b => b.RejectUnknownFields = true);
        await Host.Admin.ResetAsync(Ct);

        (await Host.Admin.GetBehaviorAsync(Ct)).Should().BeEquivalentTo(configured);
    }
}

public sealed class ConcurrencyTests : FakeTestBase
{
    [Fact]
    public async Task Parallel_registrations_get_unique_ids_and_nothing_is_lost()
    {
        var tasks = Enumerable.Range(0, 60).Select(i => RegisterEmployeeAsync(Payloads.Employee($"E-{i}", $"{10_000_000_000L + i}")));

        var responses = await Task.WhenAll(tasks);

        responses.Select(r => (long)r["body"]!["id"]!).Distinct().Should().HaveCount(60);
        (await StateAsync()).Employees.Should().HaveCount(60);
        (await GetJsonAsync("/employee/find-all?size=100"))["totalElements"]!.GetValue<int>().Should().Be(60);
    }

    [Fact]
    public async Task Parallel_registrations_of_the_same_external_id_create_exactly_one_employee()
    {
        var tasks = Enumerable.Range(0, 20).Select(_ => RegisterEmployeeAsync(Payloads.Employee("E-SAME")));

        var responses = await Task.WhenAll(tasks);

        responses.Count(r => (int)r["statusCodeValue"]! == 201).Should().Be(1);
        responses.Count(r => (string?)r["body"]!["error"] == "already_exists").Should().Be(19);
        (await StateAsync()).Employees.Should().ContainSingle();
    }

    [Fact]
    public async Task Parallel_overlapping_adjustments_are_serialized_by_the_store()
    {
        await CreateEmployeeAsync("E-1");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RegisterVacationAsync("E-1", Payloads.Jan2024, Payloads.Jan2024 + (5 * Payloads.Day))));

        responses.Count(r => (bool)r["registered"]!).Should().Be(1);
        (await StateAsync()).Adjustments.Should().ContainSingle();
    }
}
