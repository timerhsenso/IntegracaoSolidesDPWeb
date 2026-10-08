using System.Net;
using System.Text.RegularExpressions;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web;
using IntegracaoSolidesDP.Web.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>
/// Aplicação Web de verdade (TestServer) contra o SQL Server do Testcontainers.
/// O login é feito pelo formulário, com o token antiforgery, como no navegador.
/// </summary>
public sealed partial class WebHarness(SqlServerFixture db) : IAsyncDisposable
{
    public const string Senha = "SenhaForte123";

    private readonly WebApplicationFactory<WebApp> _factory = new WebApplicationFactory<WebApp>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Rhu", db.ConnectionString);
        builder.UseSetting("Web:InstanceName", "default");
    });

    public HttpClient Cliente() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    public async Task<string> CriarUsuarioAsync(string perfil, bool deveTrocarSenha = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var login = $"{perfil.ToLowerInvariant()}.{Guid.NewGuid():N}"[..24];
        var user = new ApplicationUser
        {
            UserName = login,
            NomeCompleto = $"Teste {perfil}",
            Ativo = true,
            DeveTrocarSenha = deveTrocarSenha,
            CriadoEm = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, Senha)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, perfil)).Succeeded.Should().BeTrue();
        return login;
    }

    /// <summary>Cliente já logado como <paramref name="login"/>.</summary>
    public async Task<HttpClient> EntrarAsync(string login, string senha = Senha)
    {
        var client = Cliente();
        var response = await PostFormAsync(client, "/Conta/Entrar", "/Conta/Entrar", new() { ["Usuario"] = login, ["Senha"] = senha });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return client;
    }

    /// <summary>Abre <paramref name="paginaComFormulario"/> para pegar o token antiforgery e envia o POST.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string paginaComFormulario, string url, Dictionary<string, string> campos)
    {
        var html = await client.GetStringAsync(paginaComFormulario);
        var token = TokenRegex().Match(html);
        token.Success.Should().BeTrue($"a página {paginaComFormulario} deveria ter o token antiforgery");
        campos["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value);
        return await client.PostAsync(url, new FormUrlEncodedContent(campos));
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
