using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Erros;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Tela de erro (UseExceptionHandler): registra a exceção em solidesdp.erro_aplicacao e mostra o código (E-1027) para o
/// usuário informar ao suporte. Nada técnico aparece na tela. Sem banco, a tela sai sem o código (o erro fica no log).
/// Com o envio ligado e o perfil permitindo, o usuário conta o que estava fazendo e pede o envio ao suporte.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErroController(
    IErroStore erros,
    AcessoErros acesso,
    Formatador formatador,
    Auditoria auditoria,
    TimeProvider clock,
    ILogger<ErroController> logger) : Controller
{
    [Route("/Erro")]
    public async Task<IActionResult> Index()
    {
        var falha = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (falha?.Error is not { } excecao)
        {
            return View(new ErroViewModel());
        }

        long? id = null;
        try
        {
            var erro = ErroDeLog.Criar(
                OrigensErro.Web, falha.Endpoint?.DisplayName ?? falha.Path, string.Empty, excecao.Message, excecao, cdempresa: null,
                clock.GetUtcNow());
            erro = CapturaErrosSink.DaRequisicao(erro, HttpContext) with { Tela = $"{HttpContext.Request.Method} {falha.Path}" };
            id = await erros.RegistrarAsync(erro, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível registrar o erro da tela {Tela}", falha.Path);
        }

        ErroViewModel modelo;
        try
        {
            modelo = await MontarAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A tela de erro não pode depender do banco.
            logger.LogWarning(ex, "Não foi possível ler a configuração dos erros para a tela de erro");
            modelo = new ErroViewModel { Codigo = id is { } n ? CodigoErro.De(n) : null, Id = id };
        }

        return View(modelo);
    }

    /// <summary>
    /// "Enviar ao suporte" da tela de erro. No automático o erro já é avisado sozinho: o comentário vai junto (se o aviso
    /// já saiu, sai de novo com o comentário).
    /// </summary>
    [HttpPost("/Erro/Enviar")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enviar(long id, string? comentario)
    {
        var ct = HttpContext.RequestAborted;
        if (User.Identity?.IsAuthenticated != true || !await acesso.PodeEnviarAsync(User, ct))
        {
            return Forbid();
        }

        var erro = await erros.ObterAsync(id, ct);
        if (erro is null)
        {
            return NotFound();
        }

        var configuracao = await acesso.ConfiguracaoAsync(ct);
        var automatico = configuracao.ModoEnvio == ModosEnvioErro.Automatico
                         && erro.Situacao == SituacoesErro.Aberto
                         && (erro.Origem == OrigensErro.Web ? configuracao.OrigemWeb : configuracao.OrigemServico);
        var pedirEnvio = !automatico || erro.UltimoAviso is not null;
        if (!pedirEnvio && string.IsNullOrWhiteSpace(comentario))
        {
            return View(nameof(Index), new ErroViewModel { Codigo = erro.Codigo, Enviado = "O suporte é avisado automaticamente." });
        }

        var linha = ErrosController.LinhaComentario(formatador, HttpContext, comentario, clock.GetUtcNow());
        await erros.ComentarAsync(id, HttpContext.Usuario(), linha, pedirEnvio, ct);
        await auditoria.RegistrarAsync(
            pedirEnvio ? AcoesAuditoria.ErroEnvioSolicitado : AcoesAuditoria.ErroComentado, ErrosController.Comentado(erro.Codigo, comentario), ct: ct);
        return View(nameof(Index), new ErroViewModel
        {
            Codigo = erro.Codigo,
            Enviado = pedirEnvio
                ? "Pronto: o erro vai para o suporte em alguns instantes. Obrigado por avisar."
                : "Obrigado: o comentário vai junto no aviso ao suporte.",
        });
    }

    private async Task<ErroViewModel> MontarAsync(long? id, CancellationToken ct)
    {
        var configuracao = await acesso.ConfiguracaoAsync(ct);
        return new ErroViewModel
        {
            Codigo = id is { } n ? CodigoErro.De(n) : null,
            Id = id,
            ModoEnvio = configuracao.ModoEnvio,
            AvisoAutomatico = configuracao.ModoEnvio == ModosEnvioErro.Automatico && configuracao.OrigemWeb,
            PodeEnviar = id is not null && User.Identity?.IsAuthenticated == true && await acesso.PodeEnviarAsync(User, ct),
        };
    }
}
