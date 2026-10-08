using System.Diagnostics;

namespace SolidesDP.Fake.Tests;

public sealed class FaultTests : FakeTestBase
{
    [Fact]
    public async Task Status_fault_answers_429_with_retry_after_and_is_consumed_after_times()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Method = "GET", Path = "/companies", Times = 2, Kind = FaultKind.Status, Status = 429, RetryAfterSeconds = 7, Message = "slow down" }, Ct);

        using var first = await GetAsync("/companies");
        using var second = await GetAsync("/companies");
        using var third = await GetAsync("/companies");

        first.StatusCode.Should().Be((HttpStatusCode)429);
        first.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(7));
        AssertSpringError(await ReadAsync(first), 429, "/companies");
        (await ReadAsync(second))["message"]!.GetValue<string>().Should().Be("slow down");
        second.StatusCode.Should().Be((HttpStatusCode)429);
        third.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Host.Admin.GetFaultsAsync(Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Status_fault_does_not_execute_the_operation(int status)
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/employee/register", Kind = FaultKind.Status, Status = status }, Ct);

        using var failed = await PostAsync("/employee/register", Payloads.Employee("E-1"));

        failed.StatusCode.Should().Be((HttpStatusCode)status);
        failed.Headers.Contains("Retry-After").Should().BeFalse();
        (await StateAsync()).Employees.Should().BeEmpty();
        (await RegisterEmployeeAsync(Payloads.Employee("E-1")))["statusCodeValue"]!.GetValue<int>().Should().Be(201);
    }

    [Fact]
    public async Task Status_defaults_to_500_and_times_defaults_to_1()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test" }, Ct);

        using var first = await GetAsync("/test");
        using var second = await GetAsync("/test");

        first.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rules_match_by_method_and_by_exact_or_prefix_path()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Method = "POST", Path = "/employee/*", Times = 5, Status = 503 }, Ct);

        using var get = await GetAsync("/employee/find-all");
        using var postOtherPrefix = await PostAsync("/job-role/register", new JsonObject { ["description"] = "A" });
        using var postMatching = await PostAsync("/employee/dismiss", Payloads.Dismiss("E-1"));

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        postOtherPrefix.StatusCode.Should().Be(HttpStatusCode.OK);
        postMatching.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Host.Admin.GetFaultsAsync(Ct)).Single().Remaining.Should().Be(4);
    }

    [Fact]
    public async Task Exact_path_does_not_match_longer_paths()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/adjustment", Status = 503 }, Ct);

        using var response = await GetAsync("/adjustment/find-all");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rules_are_consumed_in_the_order_they_were_added_and_unlimited_rules_never_run_out()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Status = 503 }, Ct);
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Times = -1, Status = 429 }, Ct);

        var statuses = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await GetAsync("/test");
            statuses.Add((int)response.StatusCode);
        }

        statuses.Should().Equal(503, 429, 429, 429);
        (await Host.Admin.GetFaultsAsync(Ct)).Single().Remaining.Should().Be(-1);
    }

    [Fact]
    public async Task Delay_fault_waits_and_then_processes_normally()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/employee/register", Kind = FaultKind.Delay, DelayMs = 400 }, Ct);

        var watch = Stopwatch.StartNew();
        var delayed = await RegisterEmployeeAsync(Payloads.Employee("E-1"));
        var delayedFor = watch.Elapsed;
        var fast = await RegisterEmployeeAsync(Payloads.Employee("E-2"));

        delayedFor.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        (await Host.Admin.GetFaultsAsync(Ct)).Should().BeEmpty(); // consumida: o proximo nao atrasa
        delayed["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        fast["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        (await StateAsync()).Employees.Should().HaveCount(2);
    }

    [Fact]
    public async Task ErrorInBody_returns_the_response_entity_error_with_http_200_and_does_not_execute_the_operation()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/employee/register", Kind = FaultKind.ErrorInBody, Message = "falha de negocio simulada" }, Ct);

        var response = await RegisterEmployeeAsync(Payloads.Employee("E-1")); // PostJsonAsync exige HTTP 200

        AssertEnvelopeError(response, "injected_fault", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Be("falha de negocio simulada");
        (await StateAsync()).Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task ErrorInBody_on_a_launch_endpoint_returns_registered_false_with_http_200()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/adjustment/register", Kind = FaultKind.ErrorInBody }, Ct);
        await CreateEmployeeAsync("E-1");

        var response = await RegisterVacationAsync("E-1", Payloads.Jan2024, Payloads.Jan2024 + Payloads.Day);

        AssertLaunchError(response);
        (await StateAsync()).Adjustments.Should().BeEmpty();
    }

    [Fact]
    public async Task ErrorInBody_follows_the_current_error_style_and_uses_the_given_status()
    {
        await SetBehaviorAsync(b => b.ErrorStyle = ErrorStyle.Http);
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/employee/register", Kind = FaultKind.ErrorInBody, Status = 409 }, Ct);
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/companies", Kind = FaultKind.ErrorInBody, RetryAfterSeconds = 2 }, Ct);

        using var employee = await PostAsync("/employee/register", Payloads.Employee("E-1"));
        using var companies = await GetAsync("/companies");

        employee.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertSpringError(await ReadAsync(employee), 409, "/employee/register");
        companies.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        companies.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ErrorInBody_on_an_endpoint_without_an_in_body_shape_is_a_spring_4xx()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/companies", Kind = FaultKind.ErrorInBody, Status = 502 }, Ct);

        using var response = await GetAsync("/companies");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        AssertSpringError(await ReadAsync(response), 502, "/companies");
    }

    [Fact]
    public async Task CommitThenDrop_persists_the_operation_but_the_client_never_gets_the_response()
    {
        await CreateEmployeeAsync("E-1");
        await Host.Admin.AddFaultAsync(new FaultRule { Method = "POST", Path = "/adjustment/register", Kind = FaultKind.CommitThenDrop }, Ct);

        var act = async () => await PostAsync("/adjustment/register", Payloads.Vacation("E-1", Payloads.Jan2024, Payloads.Jan2024 + (9 * Payloads.Day)));

        await act.Should().ThrowAsync<OperationCanceledException>(); // TestServer; no Kestrel real seria HttpRequestException (ver RealServerTests)
        var adjustments = (await StateAsync()).Adjustments;
        adjustments.Should().ContainSingle();
        adjustments[0].EmployeeId.Should().Be(10000);
    }

    [Fact]
    public async Task A_retry_after_CommitThenDrop_does_not_create_a_duplicate_vacation()
    {
        await CreateEmployeeAsync("E-1");
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/adjustment/register", Kind = FaultKind.CommitThenDrop }, Ct);
        var payload = Payloads.Vacation("E-1", Payloads.Jan2024, Payloads.Jan2024 + (9 * Payloads.Day));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync("/adjustment/register", payload));
        var retry = await PostJsonAsync("/adjustment/register", payload);

        AssertLaunchError(retry); // o fornecedor recusa por sobreposicao; o cliente tem que reconciliar via find-all
        retry["message"]!.GetValue<string>().Should().Contain("overlaps");
        (await GetJsonAsync("/adjustment/find-all?employeeId=10000"))["totalElements"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task CommitThenDrop_is_journaled_with_the_status_the_fake_produced()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/job-role/register", Kind = FaultKind.CommitThenDrop }, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostAsync("/job-role/register", new JsonObject { ["description"] = "A" }));

        var entry = (await Host.Admin.GetRequestsAsync(Ct)).Single();
        entry.Path.Should().Be("/job-role/register");
        entry.Status.Should().Be(200);
        entry.Fault.Should().Be("CommitThenDrop");
        (await StateAsync()).JobRoles.Should().ContainSingle();
    }

    [Fact]
    public async Task Unauthenticated_requests_do_not_consume_faults()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Status = 503 }, Ct);

        using var anonymous = await SendWithAuthorizationAsync("/test", null);
        using var authenticated = await GetAsync("/test");

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Faults_never_apply_to_admin_endpoints_and_can_be_cleared_or_reset()
    {
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/_fake/*", Times = 5, Status = 503 }, Ct);
        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Times = 5, Status = 503 }, Ct);

        (await Host.Admin.GetFaultsAsync(Ct)).Should().HaveCount(2);
        await Host.Admin.ClearFaultsAsync(Ct);
        (await Host.Admin.GetFaultsAsync(Ct)).Should().BeEmpty();

        await Host.Admin.AddFaultAsync(new FaultRule { Path = "/test", Times = 5, Status = 503 }, Ct);
        await Host.Admin.ResetAsync(Ct);
        using var response = await GetAsync("/test");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"kind":"Status"}""")] // sem path
    [InlineData("""{"path":"test"}""")] // sem barra inicial
    [InlineData("""{"path":"/test","kind":"Explode"}""")]
    [InlineData("""{"path":"/test","times":0}""")]
    [InlineData("""{"path":"/test","status":42}""")]
    [InlineData("""{"path":"/test","delayMs":-1}""")]
    [InlineData("""{"path":"/test","retryAfter":3}""")] // propriedade desconhecida
    [InlineData("not json")]
    public async Task Invalid_fault_rules_are_rejected(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await Host.Anonymous.PostAsync(Rel("/_fake/faults"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400, "/_fake/faults");
        (await Host.Admin.GetFaultsAsync(Ct)).Should().BeEmpty();
    }
}
