using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Cria o primeiro administrador. Só existe enquanto não há nenhum usuário e só atende
/// quem acessa pelo próprio servidor (https://localhost/...).
/// </summary>
[AllowAnonymous]
public sealed class PrimeiroAcessoController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    Auditoria auditoria,
    TimeProvider clock) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (await users.Users.AnyAsync(HttpContext.RequestAborted))
        {
            return RedirectToAction("Entrar", "Conta");
        }

        return HttpContext.IsLocal() ? View(new PrimeiroAcessoViewModel()) : View("SomenteLocal");
    }

    [HttpPost]
    public async Task<IActionResult> Index(PrimeiroAcessoViewModel model)
    {
        if (await users.Users.AnyAsync(HttpContext.RequestAborted) || !HttpContext.IsLocal())
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Usuario.Trim(),
            NomeCompleto = model.NomeCompleto.Trim(),
            Ativo = true,
            CriadoEm = clock.GetUtcNow(),
        };
        var result = await users.CreateAsync(user, model.Senha);
        if (result.Succeeded)
        {
            result = await users.AddToRoleAsync(user, Perfis.Admin);
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        await auditoria.RegistrarAsync(AcoesAuditoria.PrimeiroAdmin, $"administrador {user.UserName}", user.UserName);
        await signIn.SignInAsync(user, isPersistent: false);
        return RedirectToAction("Index", "Painel");
    }
}
