using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Identity;

/// <summary>Acrescenta ao cookie o nome do usuário e a obrigação de trocar a senha.</summary>
public sealed class AppClaimsFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(ClaimsWeb.Nome, string.IsNullOrWhiteSpace(user.NomeCompleto) ? user.UserName ?? string.Empty : user.NomeCompleto));
        if (user.DeveTrocarSenha)
        {
            identity.AddClaim(new Claim(ClaimsWeb.TrocaSenha, "1"));
        }

        return identity;
    }
}
