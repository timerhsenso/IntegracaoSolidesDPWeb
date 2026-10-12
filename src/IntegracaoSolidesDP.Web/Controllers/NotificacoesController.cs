using System.Globalization;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Avisos por e-mail: servidor SMTP (senha cifrada, nunca exibida), quando avisar e quem recebe. Só Admin.
/// Quem envia é o serviço; o teste é um pedido ao serviço, como os outros.
/// </summary>
[ExigeGestaoPreparada]
[Authorize(Policy = Politicas.Administrar)]
public sealed class NotificacoesController(
    INotificacaoStore notificacoes,
    IManagementStore store,
    EmpresasRepository empresas,
    ITokenProtector tokens,
    Auditoria auditoria,
    IOptions<WebOptions> options) : Controller
{
    private const int EnviosNaTela = 30;

    public async Task<IActionResult> Index() => View(await MontarAsync(form: null, HttpContext.RequestAborted));

    [HttpPost]
    public async Task<IActionResult> Salvar([Bind(Prefix = "Form")] NotificacaoForm form)
    {
        var ct = HttpContext.RequestAborted;
        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await MontarAsync(form, ct));
        }

        var atual = await notificacoes.ConfiguracaoAsync(ct);
        var trocarSenha = form.ApagarSenha || !string.IsNullOrEmpty(form.Senha);
        var senha = form.ApagarSenha || string.IsNullOrEmpty(form.Senha) ? null : tokens.Protect(form.Senha);
        var configuracao = form.ParaConfiguracao(senha);
        await notificacoes.SalvarConfiguracaoAsync(configuracao, trocarSenha, HttpContext.Usuario(), ct);

        var senhaTexto = form.ApagarSenha ? "senha apagada" : !string.IsNullOrEmpty(form.Senha) ? "senha trocada" : "senha mantida";
        var detalhe = string.Join("; ",
            configuracao.Ativo ? "ligado" : "desligado",
            $"{configuracao.Servidor}:{configuracao.Porta.ToString(CultureInfo.InvariantCulture)}",
            $"usuário {configuracao.Usuario ?? "-"}",
            senhaTexto,
            $"resumo {configuracao.HoraResumo.ToString("hh\\:mm", CultureInfo.InvariantCulture)}{(configuracao.SomenteDiasUteis ? " em dias úteis" : "")}",
            $"lembrete {configuracao.LembreteHoras.ToString(CultureInfo.InvariantCulture)}h",
            configuracao.NotificarSimulacao ? "simulações avisam" : "simulações não avisam");
        await auditoria.RegistrarAsync(AcoesAuditoria.NotificacaoAlterada, detalhe, ct: ct);

        TempData["Sucesso"] = atual is null
            ? "Configuração gravada. Cadastre os destinatários e use \"Enviar e-mail de teste\"."
            : "Configuração gravada. O serviço passa a usá-la na próxima execução.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Destinatario([Bind(Prefix = "NovoDestinatario")] DestinatarioForm form)
    {
        var ct = HttpContext.RequestAborted;
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(form.Email))
        {
            TempData["Erro"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).DefaultIfEmpty("Informe o e-mail."));
            return RedirectToAction(nameof(Index));
        }

        if (!form.RecebeFalhas && !form.RecebeResumo)
        {
            TempData["Erro"] = "Marque o que o destinatário recebe: falhas, resumo de pendências ou os dois.";
            return RedirectToAction(nameof(Index));
        }

        var email = form.Email.Trim();
        var existentes = await notificacoes.DestinatariosAsync(ct);
        if (form.Id == 0 && existentes.Any(d => string.Equals(d.Email, email, StringComparison.OrdinalIgnoreCase) && d.Cdempresa == form.Cdempresa))
        {
            TempData["Erro"] = $"{email} já está cadastrado para {(form.Cdempresa is { } e ? $"a empresa {e}" : "todas as empresas")}.";
            return RedirectToAction(nameof(Index));
        }

        var id = await notificacoes.SalvarDestinatarioAsync(new DestinatarioNotificacao
        {
            Id = form.Id,
            Email = email,
            Cdempresa = form.Cdempresa,
            RecebeFalhas = form.RecebeFalhas,
            RecebeResumo = form.RecebeResumo,
            Ativo = true,
        }, HttpContext.Usuario(), ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.NotificacaoDestinatario, Descrever(id, email, form.Cdempresa, form.RecebeFalhas, form.RecebeResumo, ativo: true), ct: ct);
        TempData["Sucesso"] = $"Destinatário {email} gravado.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Desativa ou reativa um destinatário (nada é apagado: fica o histórico).</summary>
    [HttpPost]
    public async Task<IActionResult> AlternarDestinatario(long id)
    {
        var ct = HttpContext.RequestAborted;
        var destinatario = (await notificacoes.DestinatariosAsync(ct)).FirstOrDefault(d => d.Id == id);
        if (destinatario is null)
        {
            return NotFound();
        }

        var alterado = destinatario with { Ativo = !destinatario.Ativo };
        await notificacoes.SalvarDestinatarioAsync(alterado, HttpContext.Usuario(), ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.NotificacaoDestinatario,
            Descrever(id, alterado.Email, alterado.Cdempresa, alterado.RecebeFalhas, alterado.RecebeResumo, alterado.Ativo), ct: ct);
        TempData["Sucesso"] = alterado.Ativo ? $"{alterado.Email} volta a receber os avisos." : $"{alterado.Email} não recebe mais os avisos.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Pede ao serviço um e-mail de teste para os destinatários ativos. O resultado aparece em Pedidos ao serviço.</summary>
    [HttpPost]
    public async Task<IActionResult> Testar()
    {
        var ct = HttpContext.RequestAborted;
        var id = await store.EnqueueCommandAsync(options.Value.InstanceName, CommandTypes.TestarEmail, HttpContext.Usuario(), cdempresa: null, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.ComandoSolicitado,
            $"{CommandTypes.TestarEmail} (pedido {id.ToString(CultureInfo.InvariantCulture)})", ct: ct);
        TempData["Sucesso"] = "Pedido de e-mail de teste registrado. O serviço o atende em alguns segundos; o resultado aparece abaixo.";
        return RedirectToAction("Detalhe", "Comandos", new { id });
    }

    private async Task<NotificacoesViewModel> MontarAsync(NotificacaoForm? form, CancellationToken ct)
    {
        var atual = await notificacoes.ConfiguracaoAsync(ct);
        return new NotificacoesViewModel
        {
            Form = form ?? NotificacaoForm.De(atual),
            Atual = atual,
            TemSenha = atual?.SenhaCifrada is { Length: > 0 },
            Destinatarios = await notificacoes.DestinatariosAsync(ct),
            Envios = await notificacoes.EnviosRecentesAsync(EnviosNaTela, ct),
            Empresas = await empresas.EmpresasAtivasAsync(ct),
        };
    }

    private static string Descrever(long id, string email, int? cdempresa, bool falhas, bool resumo, bool ativo) =>
        string.Join(", ",
            $"destinatário {id.ToString(CultureInfo.InvariantCulture)}: {email}",
            cdempresa is { } e ? $"empresa {e.ToString(CultureInfo.InvariantCulture)}" : "todas as empresas",
            falhas ? "recebe falhas" : "sem falhas",
            resumo ? "recebe resumo" : "sem resumo",
            ativo ? "ativo" : "desativado");
}
