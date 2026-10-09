using System.Net.Http.Headers;

namespace SolidesDP.Fake.Tests;

/// <summary>Fake:IsolarContasPorToken: cada token é uma conta do Sólides DP, com os seus dados.</summary>
public sealed class AccountIsolationTests() : FakeTestBase(new Dictionary<string, string?>
{
    ["Fake:Tokens:0"] = FakeHost.Token,
    ["Fake:Tokens:1"] = "outra-conta",
    ["Fake:IsolarContasPorToken"] = "true",
})
{
    [Fact]
    public async Task Each_token_sees_only_its_own_employees()
    {
        await RegisterEmployeeAsync(Payloads.Employee("A-1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Rel("/employee/find-all"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "outra-conta");

        using var response = await Host.Anonymous.SendAsync(request, Ct);
        var page = await ReadAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        page["content"]!.AsArray().Should().BeEmpty();
        (await Host.Admin.GetStateAsync(Ct)).Employees.Should().ContainSingle();
        (await Host.Admin.GetStateAsync("outra-conta", Ct)).Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_account_starts_from_the_seed_and_reset_clears_them_all()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Rel("/work-schedule"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "outra-conta");
        using var response = await Host.Anonymous.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Host.Admin.GetStateAsync("outra-conta", Ct)).WorkSchedules.Should().NotBeEmpty();

        await Host.Admin.ResetAsync(Ct);
        (await Host.Admin.GetStateAsync("outra-conta", Ct)).Employees.Should().BeEmpty();
    }
}
