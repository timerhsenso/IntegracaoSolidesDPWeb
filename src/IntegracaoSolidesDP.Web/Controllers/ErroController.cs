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
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErroController(IErroStore erros, TimeProvider clock, ILogger<ErroController> logger) : Controller
{
    [Route("/Erro")]
    public async Task<IActionResult> Index()
    {
        var falha = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (falha?.Error is not { } excecao)
        {
            return View(new ErroViewModel(null));
        }

        string? codigo = null;
        try
        {
            var erro = ErroDeLog.Criar(
                OrigensErro.Web, falha.Endpoint?.DisplayName ?? falha.Path, string.Empty, excecao.Message, excecao, cdempresa: null,
                clock.GetUtcNow());
            erro = CapturaErrosSink.DaRequisicao(erro, HttpContext) with { Tela = $"{HttpContext.Request.Method} {falha.Path}" };
            codigo = await erros.RegistrarAsync(erro, CancellationToken.None) is { } id ? CodigoErro.De(id) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível registrar o erro da tela {Tela}", falha.Path);
        }

        return View(new ErroViewModel(codigo));
    }
}
