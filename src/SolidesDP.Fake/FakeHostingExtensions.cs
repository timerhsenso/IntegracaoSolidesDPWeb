using Microsoft.Extensions.DependencyInjection.Extensions;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Endpoints;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake;

/// <summary>Composicao do fake: servicos (<see cref="AddFake"/>) e pipeline/rotas (<see cref="UseFake"/>).</summary>
public static class FakeHostingExtensions
{
    /// <summary>Registra opcoes (secao <c>Fake</c>), store, diario de requests e registro de falhas.</summary>
    public static IServiceCollection AddFake(this IServiceCollection services)
    {
        services.AddOptions<FakeOptions>().BindConfiguration(FakeOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FakeStore>();
        services.AddSingleton<RequestJournal>();
        services.AddSingleton<FaultRegistry>();
        return services;
    }

    /// <summary>Monta o pipeline (diario, erros, autenticacao, falhas) e mapeia a API do fornecedor e o <c>/_fake</c>.</summary>
    public static WebApplication UseFake(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<FakeStore>(); // seed invalido derruba o startup, nao o primeiro request

        app.UseMiddleware<JournalMiddleware>();
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseStatusCodePages(context =>
        {
            var http = context.HttpContext;
            var status = http.Response.StatusCode;
            var message = status == StatusCodes.Status405MethodNotAllowed
                ? $"Request method '{http.Request.Method}' not supported"
                : "No message available";
            return ApiResults.WriteSpringAsync(http, status, message);
        });
        app.UseRouting();
        app.UseMiddleware<AuthMiddleware>();
        app.UseMiddleware<FaultMiddleware>();

        app.MapAdminEndpoints();
        app.MapReferenceEndpoints();
        app.MapEmployeeEndpoints();
        app.MapAdjustmentEndpoints();
        return app;
    }
}
