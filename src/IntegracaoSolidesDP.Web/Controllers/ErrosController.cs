using System.Globalization;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Erros;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Erros da aplicação (solidesdp.erro_aplicacao): Admin sempre; Operador só com "Operador vê a tela de erros" ligado em
/// Notificações. Silenciar, resolver e reabrir são do Admin e ficam na auditoria.
/// </summary>
[ExigeGestaoPreparada]
[Authorize(Policy = Politicas.Operar)]
public sealed class ErrosController(IErroStore erros, AcessoErros acesso, Auditoria auditoria) : Controller
{
    /// <summary>Erros mostrados na lista (os de ocorrência mais recente).</summary>
    public const int Limite = 300;

    public async Task<IActionResult> Index(string? situacao, string? origem)
    {
        var ct = HttpContext.RequestAborted;
        if (!await acesso.PodeVerAsync(User, ct))
        {
            return Forbid();
        }

        situacao = situacao is SituacoesErro.Aberto or SituacoesErro.Silenciado or SituacoesErro.Resolvido ? situacao : null;
        origem = origem is OrigensErro.Web or OrigensErro.Servico ? origem : null;
        return View(new ErrosViewModel
        {
            Erros = await erros.ListarAsync(situacao, origem, Limite, ct),
            Situacao = situacao,
            Origem = origem,
            PodeAlterar = User.IsInRole(Perfis.Admin),
            Limite = Limite,
        });
    }

    public async Task<IActionResult> Detalhe(long id)
    {
        var ct = HttpContext.RequestAborted;
        if (!await acesso.PodeVerAsync(User, ct))
        {
            return Forbid();
        }

        var erro = await erros.ObterAsync(id, ct);
        return erro is null
            ? NotFound()
            : View(new ErroDetalheViewModel { Erro = erro, PodeAlterar = User.IsInRole(Perfis.Admin) });
    }

    /// <summary>Conhecido: continua sendo registrado e contado, mas não gera e-mail.</summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public Task<IActionResult> Silenciar(long id) =>
        MudarAsync(id, SituacoesErro.Silenciado, AcoesAuditoria.ErroSilenciado, "silenciado: continua sendo registrado, sem e-mail");

    /// <summary>Corrigido: se acontecer de novo, volta a ficar aberto.</summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public Task<IActionResult> Resolver(long id) =>
        MudarAsync(id, SituacoesErro.Resolvido, AcoesAuditoria.ErroResolvido, "marcado como resolvido: se acontecer de novo, volta a ficar aberto");

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public Task<IActionResult> Reabrir(long id) =>
        MudarAsync(id, SituacoesErro.Aberto, AcoesAuditoria.ErroReaberto, "reaberto");

    private async Task<IActionResult> MudarAsync(long id, string situacao, string acao, string texto)
    {
        var ct = HttpContext.RequestAborted;
        var erro = await erros.ObterAsync(id, ct);
        if (erro is null)
        {
            return NotFound();
        }

        if (await erros.MudarSituacaoAsync(id, situacao, HttpContext.Usuario(), ct))
        {
            acesso.Invalidar();
            await auditoria.RegistrarAsync(acao, $"{erro.Codigo} ({Resumo(erro.Mensagem)})", ct: ct);
            TempData["Sucesso"] = $"Erro {erro.Codigo} {texto}.";
        }

        return RedirectToAction(nameof(Detalhe), new { id });
    }

    private static string Resumo(string mensagem) =>
        mensagem.Length <= 120 ? mensagem : string.Concat(mensagem.AsSpan(0, 117), "...");

    /// <summary>Texto para a auditoria das opções gravadas no card de Notificações.</summary>
    internal static string Descrever(ConfiguracaoErros c) =>
        string.Join("; ",
            c.ExecucaoCaida ? "execução que caiu é erro" : "execução que caiu não é erro",
            c.OperadorVe ? "Operador vê a tela" : "só Admin vê a tela",
            c.RetencaoDias > 0 ? $"guarda {c.RetencaoDias.ToString(CultureInfo.InvariantCulture)} dias" : "guarda para sempre");
}
