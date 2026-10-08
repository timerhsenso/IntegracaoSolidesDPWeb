using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Source;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Infrastructure;

public static class WebRegistration
{
    public static IServiceCollection AddIntegracaoSolidesDPWeb(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebOptions>().Bind(configuration.GetSection(WebOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        // Tabelas do serviço (Dapper), no mesmo bd_rhu_adn. A connection string é lida ao resolver
        // (e não no registro) para valer a configuração final, inclusive a dos testes.
        services.AddSingleton(sp => new ConnectionFactory(ConnectionString(sp.GetRequiredService<IConfiguration>())));
        services.AddSingleton<IManagementStore, SqlManagementStore>();
        services.AddScoped<PainelRepository>();
        services.AddScoped<Auditoria>();
        services.AddScoped<GestaoService>();
        services.AddSingleton<Formatador>();

        // Login: ASP.NET Core Identity no schema solidesdp_auth.
        services.AddDbContext<AuthDbContext>((sp, options) => options.UseSqlServer(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("Rhu"),
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema)));

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsFactory>()
            .AddErrorDescriber<IdentityErrorDescriberPtBr>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "IntegracaoSolidesDP.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/Conta/Entrar";
            options.LogoutPath = "/Conta/Sair";
            options.AccessDeniedPath = "/Conta/AcessoNegado";
        });

        // Usuário desativado ou com perfil alterado perde o acesso em até 1 minuto.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        // Chaves do cookie no banco: o login sobrevive à reciclagem do app pool do IIS.
        services.AddDataProtection()
            .SetApplicationName("IntegracaoSolidesDP.Web")
            .PersistKeysToDbContext<AuthDbContext>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Politicas.Operar, policy => policy.RequireRole(Perfis.Operador, Perfis.Admin))
            .AddPolicy(Politicas.Administrar, policy => policy.RequireRole(Perfis.Admin));

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "IntegracaoSolidesDP.Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        services.AddControllersWithViews(options =>
        {
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            options.Filters.Add<TrocaSenhaFilter>();
        });

        services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));
        return services;
    }

    private static string ConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Rhu");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("ConnectionStrings:Rhu é obrigatório.")
            : connectionString;
    }

    public static WebApplication UseIntegracaoSolidesDPWeb(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Erro");
            app.UseHsts();
        }

        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            await next();
        });

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllerRoute("default", "{controller=Painel}/{action=Index}/{id?}");
        return app;
    }

    /// <summary>
    /// Na partida: migrations do login, perfis e tabelas da gestão. As tabelas da gestão são
    /// criadas pelo serviço; aqui é só uma garantia (sem permissão de DDL, a Web segue e avisa no painel).
    /// </summary>
    public static async Task PrepararBancoAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("IntegracaoSolidesDP.Web");

        if (services.GetRequiredService<IOptions<WebOptions>>().Value.AplicarMigrationsNoStart)
        {
            await services.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
        }

        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var perfil in Perfis.Todos)
        {
            if (!await roles.RoleExistsAsync(perfil))
            {
                await roles.CreateAsync(new IdentityRole(perfil));
            }
        }

        try
        {
            await services.GetRequiredService<IManagementStore>().EnsureSchemaAsync(CancellationToken.None);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            logger.LogWarning(ex, "Não foi possível criar as tabelas da gestão (schema solidesdp); o serviço as cria ao rodar com Gestao:Habilitada");
        }
    }
}
