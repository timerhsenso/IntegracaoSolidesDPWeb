using System.Text;

namespace SolidesDP.Fake.Tests;

/// <summary>O fake num Kestrel real (<see cref="Testing.FakeServer"/>): transporte de verdade, como o worker o veria em producao.</summary>
public sealed class RealServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_fake_answers_over_a_real_socket()
    {
        await using var server = await Testing.FakeServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient();
        using var anonymous = server.CreateClient(token: null);

        using var ok = await client.GetAsync(new Uri("/test", UriKind.Relative), Ct);
        using var unauthorized = await anonymous.GetAsync(new Uri("/test", UriKind.Relative), Ct);

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        server.BaseAddress.Host.Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task CommitThenDrop_resets_the_connection_and_the_operation_is_persisted_exactly_once()
    {
        await using var server = await Testing.FakeServer.StartAsync(cancellationToken: Ct);
        using var client = server.CreateClient();
        var admin = server.CreateAdminClient();
        using var employee = await client.PostAsync(new Uri("/employee/register", UriKind.Relative), Json(Payloads.Employee("E-1")), Ct);
        await admin.AddFaultAsync(new FaultRule { Path = "/adjustment/register", Kind = FaultKind.CommitThenDrop }, Ct);
        var vacation = Payloads.Vacation("E-1", Payloads.Jan2024, Payloads.Jan2024 + (9 * Payloads.Day));

        var drop = async () => await client.PostAsync(new Uri("/adjustment/register", UriKind.Relative), Json(vacation), Ct);

        await drop.Should().ThrowAsync<HttpRequestException>();
        (await admin.GetStateAsync(Ct)).Adjustments.Should().ContainSingle();

        // o retry do cliente recebe a recusa por sobreposicao (nao cria uma segunda ferias)
        using var retry = await client.PostAsync(new Uri("/adjustment/register", UriKind.Relative), Json(vacation), Ct);
        var body = JsonNode.Parse(await retry.Content.ReadAsStringAsync(Ct))!;
        body["registered"]!.GetValue<bool>().Should().BeFalse();
        (await admin.GetStateAsync(Ct)).Adjustments.Should().ContainSingle();
        (await admin.GetRequestsAsync(Ct)).Select(r => (r.Path, r.Fault)).Should().Contain(("/adjustment/register", "CommitThenDrop"));
    }

    [Fact]
    public async Task Settings_are_hermetic_and_applied()
    {
        await using var server = await Testing.FakeServer.StartAsync(
            new Dictionary<string, string?> { ["Fake:Tokens:0"] = "meu-token", ["Fake:Behavior:ErrorStyle"] = "Http" },
            Ct);
        using var client = server.CreateClient("meu-token");
        using var wrong = server.CreateClient();

        using var dismiss = await client.PostAsync(new Uri("/employee/dismiss", UriKind.Relative), Json(Payloads.Dismiss("NAO-EXISTE")), Ct);
        using var unauthorized = await wrong.GetAsync(new Uri("/test", UriKind.Relative), Ct);

        dismiss.StatusCode.Should().Be(HttpStatusCode.NotFound); // ErrorStyle=Http
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
