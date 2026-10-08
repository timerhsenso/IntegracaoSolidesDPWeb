using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>Pedidos ao serviço (solidesdp.comando). A Web só grava o pedido; o serviço executa e grava o resultado.</summary>
[ExigeGestaoPreparada]
public sealed class ComandosController(PainelRepository painel, GestaoService gestao) : Controller
{
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Dados() =>
        Json(await painel.ComandosAsync(DataTablesRequest.From(Request.Query), HttpContext.RequestAborted));

    [HttpPost]
    [Authorize(Policy = Politicas.Operar)]
    public async Task<IActionResult> Solicitar(string tipo)
    {
        var resultado = await gestao.SolicitarComandoAsync(tipo, HttpContext.Usuario(), HttpContext.RequestAborted);
        if (resultado.Sucesso)
        {
            TempData["Sucesso"] = $"Pedido \"{Rotulos.Comando(tipo)}\" registrado. O serviço o atende em alguns segundos.";
            return RedirectToAction(nameof(Detalhe), new { id = resultado.Id });
        }

        TempData["Erro"] = string.Join(" ", resultado.Erros);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Detalhe(long id)
    {
        var comando = await painel.ComandoAsync(id, HttpContext.RequestAborted);
        if (comando is null)
        {
            return NotFound();
        }

        return View(comando);
    }
}
