using System.Globalization;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Notificacoes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Erros da aplicação (solidesdp.erro_aplicacao): Admin sempre; Operador só com "Operador vê a tela de erros" ligado em
/// Notificações. Silenciar, resolver e reabrir são do Admin e ficam na auditoria.
/// </summary>
[ExigeGestaoPreparada]
[Authorize(Policy = Politicas.Operar)]
public sealed class ErrosController(IErroStore erros, INotificacaoStore notificacoes, AcessoErros acesso, Formatador formatador, Auditoria auditoria) : Controller
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
        if (erro is null)
        {
            return NotFound();
        }

        return View(new ErroDetalheViewModel
        {
            Erro = erro,
            PodeAlterar = User.IsInRole(Perfis.Admin),
            PodeEnviar = await acesso.PodeEnviarAsync(User, ct),
            TemDestinatario = (await notificacoes.DestinatariosAsync(ct)).Any(d => d.Ativo && d.RecebeErros),
        });
    }

    /// <summary>"Enviar ao suporte" com comentário: quem envia é o serviço, no próximo ciclo (alguns segundos).</summary>
    [HttpPost]
    public async Task<IActionResult> Enviar(long id, string? comentario)
    {
        var ct = HttpContext.RequestAborted;
        if (!await acesso.PodeVerAsync(User, ct) || !await acesso.PodeEnviarAsync(User, ct))
        {
            return Forbid();
        }

        var erro = await erros.ObterAsync(id, ct);
        if (erro is null)
        {
            return NotFound();
        }

        await erros.ComentarAsync(id, HttpContext.Usuario(), LinhaComentario(formatador, HttpContext, comentario, DateTimeOffset.UtcNow), pedirEnvio: true, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.ErroEnvioSolicitado, Comentado(erro.Codigo, comentario), ct: ct);
        TempData["Sucesso"] = $"Erro {erro.Codigo}: envio ao suporte pedido. O serviço envia em alguns segundos (veja em Notificações › Últimos envios).";
        return RedirectToAction(nameof(Detalhe), new { id });
    }

    /// <summary>"14/10/2026 10:32 carlos: estava salvando a empresa 15"; null sem texto.</summary>
    internal static string? LinhaComentario(Formatador formatador, HttpContext http, string? comentario, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(formatador);
        ArgumentNullException.ThrowIfNull(http);
        return string.IsNullOrWhiteSpace(comentario) ? null : $"{formatador.Data(agora)} {http.Usuario()}: {comentario.Trim()}";
    }

    /// <summary>Texto da auditoria: o código e o começo do comentário.</summary>
    internal static string Comentado(string codigo, string? comentario) =>
        string.IsNullOrWhiteSpace(comentario) ? codigo : $"{codigo}: {Resumo(comentario.Trim())}";

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

    /// <summary>Rótulo do modo de envio.</summary>
    internal static string ModoTexto(string modo) => modo switch
    {
        ModosEnvioErro.Desligado => "desligado",
        ModosEnvioErro.Manual => "só manual",
        _ => "automático",
    };

    /// <summary>Texto para a auditoria das opções gravadas no card de Notificações.</summary>
    internal static string Descrever(ConfiguracaoErros c) =>
        string.Join("; ",
            $"envio {ModoTexto(c.ModoEnvio)}",
            $"origens: {(c.OrigemWeb ? "Web" : "")}{(c.OrigemWeb && c.OrigemServico ? " e " : "")}{(c.OrigemServico ? "serviço" : "")}{(!c.OrigemWeb && !c.OrigemServico ? "nenhuma" : "")}",
            $"resumo a cada {c.ResumoHoras.ToString(CultureInfo.InvariantCulture)}h",
            $"limite {c.LimiteDia.ToString(CultureInfo.InvariantCulture)} por dia",
            c.OperadorEnvia ? "Operador envia" : "Operador não envia",
            $"cliente {c.NomeCliente ?? "-"}",
            c.ExecucaoCaida ? "execução que caiu é erro" : "execução que caiu não é erro",
            c.OperadorVe ? "Operador vê a tela" : "só Admin vê a tela",
            c.RetencaoDias > 0 ? $"guarda {c.RetencaoDias.ToString(CultureInfo.InvariantCulture)} dias" : "guarda para sempre");
}
