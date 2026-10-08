using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>Itens da última execução que pedem ação do RH (falhas, bloqueios, ignorados, avisos, adiados).</summary>
[ExigeGestaoPreparada]
public sealed class PendenciasController(PainelRepository painel) : Controller
{
    public async Task<IActionResult> Index() =>
        View(await painel.UltimaExecucaoAsync(somenteReal: false, HttpContext.RequestAborted));
}
