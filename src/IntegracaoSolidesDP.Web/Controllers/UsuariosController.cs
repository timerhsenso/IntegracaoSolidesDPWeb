using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Usuários da Web. Nenhum usuário é excluído: é desativado (e bloqueado no Identity).
/// O administrador não altera o próprio perfil nem se desativa, para nunca ficar sem administrador.
/// </summary>
[Authorize(Policy = Politicas.Administrar)]
public sealed class UsuariosController(UserManager<ApplicationUser> users, Auditoria auditoria, TimeProvider clock) : Controller
{
    public async Task<IActionResult> Index()
    {
        var agora = clock.GetUtcNow();
        var lista = await users.Users.OrderBy(u => u.UserName).ToListAsync(HttpContext.RequestAborted);
        var itens = new List<UsuarioListaItem>(lista.Count);
        foreach (var user in lista)
        {
            var perfis = await users.GetRolesAsync(user);
            itens.Add(new UsuarioListaItem(
                user.Id,
                user.UserName ?? string.Empty,
                user.NomeCompleto,
                perfis.FirstOrDefault(),
                user.Ativo,
                user.DeveTrocarSenha,
                user.Ativo && user.LockoutEnd > agora ? user.LockoutEnd : null,
                user.CriadoEm));
        }

        return View(itens);
    }

    [HttpGet]
    public IActionResult Novo() => View(new UsuarioNovoViewModel());

    [HttpPost]
    public async Task<IActionResult> Novo(UsuarioNovoViewModel model)
    {
        if (!Perfis.Todos.Contains(model.Perfil))
        {
            ModelState.AddModelError(nameof(model.Perfil), "Perfil inválido.");
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
            DeveTrocarSenha = true,
            CriadoEm = clock.GetUtcNow(),
        };
        var result = await users.CreateAsync(user, model.Senha);
        if (result.Succeeded)
        {
            result = await users.AddToRoleAsync(user, model.Perfil);
        }

        if (!result.Succeeded)
        {
            AdicionarErros(result);
            return View(model);
        }

        await auditoria.RegistrarAsync(AcoesAuditoria.UsuarioCriado, $"{user.UserName} ({model.Perfil})");
        TempData["Sucesso"] = $"Usuário {user.UserName} criado. Ele troca a senha no primeiro acesso.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Editar(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var perfis = await users.GetRolesAsync(user);
        return View(new UsuarioEditarViewModel
        {
            Id = user.Id,
            Usuario = user.UserName,
            NomeCompleto = user.NomeCompleto,
            Perfil = perfis.FirstOrDefault() ?? Perfis.Consulta,
            Ativo = user.Ativo,
            EhOProprioUsuario = user.Id == users.GetUserId(User),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Editar(UsuarioEditarViewModel model)
    {
        var user = await users.FindByIdAsync(model.Id);
        if (user is null)
        {
            return NotFound();
        }

        var perfisAtuais = await users.GetRolesAsync(user);
        var proprio = user.Id == users.GetUserId(User);
        model.Usuario = user.UserName;
        model.Ativo = user.Ativo;
        model.EhOProprioUsuario = proprio;

        if (!Perfis.Todos.Contains(model.Perfil))
        {
            ModelState.AddModelError(nameof(model.Perfil), "Perfil inválido.");
        }
        else if (proprio && !perfisAtuais.Contains(model.Perfil))
        {
            ModelState.AddModelError(nameof(model.Perfil), "Você não pode alterar o próprio perfil.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var alteracoes = new List<string>();
        if (!string.Equals(user.NomeCompleto, model.NomeCompleto.Trim(), StringComparison.Ordinal))
        {
            alteracoes.Add($"nome: {user.NomeCompleto} → {model.NomeCompleto.Trim()}");
            user.NomeCompleto = model.NomeCompleto.Trim();
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                AdicionarErros(updated);
                return View(model);
            }
        }

        if (!perfisAtuais.SequenceEqual(new[] { model.Perfil }))
        {
            alteracoes.Add($"perfil: {string.Join(",", perfisAtuais)} → {model.Perfil}");
            var removed = await users.RemoveFromRolesAsync(user, perfisAtuais);
            var added = removed.Succeeded ? await users.AddToRoleAsync(user, model.Perfil) : removed;
            if (!added.Succeeded)
            {
                AdicionarErros(added);
                return View(model);
            }

            await users.UpdateSecurityStampAsync(user);
        }

        if (alteracoes.Count > 0)
        {
            await auditoria.RegistrarAsync(AcoesAuditoria.UsuarioAlterado, $"{user.UserName}: {string.Join("; ", alteracoes)}");
            TempData["Sucesso"] = $"Usuário {user.UserName} alterado.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> RedefinirSenha(string id, string? novaSenha)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(novaSenha))
        {
            TempData["Erro"] = "Informe a nova senha.";
            return RedirectToAction(nameof(Editar), new { id });
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, novaSenha);
        if (!result.Succeeded)
        {
            TempData["Erro"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Editar), new { id });
        }

        user.DeveTrocarSenha = true;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        if (user.Ativo)
        {
            await users.SetLockoutEndDateAsync(user, null);
        }

        await auditoria.RegistrarAsync(AcoesAuditoria.UsuarioSenhaRedefinida, user.UserName);
        TempData["Sucesso"] = $"Senha de {user.UserName} redefinida. Ele troca a senha no próximo acesso.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Desativar(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        if (user.Id == users.GetUserId(User))
        {
            TempData["Erro"] = "Você não pode desativar o próprio usuário.";
            return RedirectToAction(nameof(Index));
        }

        user.Ativo = false;
        await users.UpdateAsync(user);
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await users.UpdateSecurityStampAsync(user);
        await auditoria.RegistrarAsync(AcoesAuditoria.UsuarioDesativado, user.UserName);
        TempData["Sucesso"] = $"Usuário {user.UserName} desativado.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Ativar(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        user.Ativo = true;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await auditoria.RegistrarAsync(AcoesAuditoria.UsuarioAtivado, user.UserName);
        TempData["Sucesso"] = $"Usuário {user.UserName} ativado.";
        return RedirectToAction(nameof(Index));
    }

    private void AdicionarErros(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }
}
