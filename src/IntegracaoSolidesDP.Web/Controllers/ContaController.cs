using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegracaoSolidesDP.Web.Controllers;

public sealed class ContaController(
    SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users,
    Auditoria auditoria) : Controller
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Entrar(string? returnUrl)
    {
        if (!await users.Users.AnyAsync(HttpContext.RequestAborted))
        {
            return RedirectToAction("Index", "PrimeiroAcesso");
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Painel");
        }

        return View(new EntrarViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Entrar(EntrarViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var login = model.Usuario.Trim();
        var user = await users.FindByNameAsync(login);
        if (user is { Ativo: false })
        {
            await auditoria.RegistrarAsync(AcoesAuditoria.LoginRecusado, null, login);
            ModelState.AddModelError(string.Empty, "Usuário desativado. Procure o administrador.");
            return View(model);
        }

        var result = await signIn.PasswordSignInAsync(login, model.Senha, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded && user is not null)
        {
            await auditoria.RegistrarAsync(AcoesAuditoria.Login, null, user.UserName);
            if (user.DeveTrocarSenha)
            {
                return RedirectToAction(nameof(AlterarSenha));
            }

            if (Url.IsLocalUrl(model.ReturnUrl))
            {
                return LocalRedirect(model.ReturnUrl);
            }

            return RedirectToAction("Index", "Painel");
        }

        if (result.IsLockedOut)
        {
            await auditoria.RegistrarAsync(AcoesAuditoria.LoginBloqueado, null, login);
            ModelState.AddModelError(string.Empty, "Usuário bloqueado por excesso de tentativas. Tente de novo em 15 minutos.");
        }
        else
        {
            await auditoria.RegistrarAsync(AcoesAuditoria.LoginFalhou, null, login);
            ModelState.AddModelError(string.Empty, "Usuário ou senha inválidos.");
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Sair()
    {
        await auditoria.RegistrarAsync(AcoesAuditoria.Logout);
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Entrar));
    }

    [HttpGet]
    public IActionResult AlterarSenha() => View(new AlterarSenhaViewModel());

    [HttpPost]
    public async Task<IActionResult> AlterarSenha(AlterarSenhaViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await users.ChangePasswordAsync(user, model.SenhaAtual, model.NovaSenha);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        user.DeveTrocarSenha = false;
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        await auditoria.RegistrarAsync(AcoesAuditoria.SenhaAlterada);
        TempData["Sucesso"] = "Senha alterada.";
        return RedirectToAction("Index", "Painel");
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AcessoNegado() => View();
}
