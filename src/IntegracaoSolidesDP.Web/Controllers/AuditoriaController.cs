using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

[Authorize(Policy = Politicas.Administrar)]
[ExigeGestaoPreparada]
public sealed class AuditoriaController(PainelRepository painel) : Controller
{
    public IActionResult Index() => View();

    [HttpGet]
    public async Task<IActionResult> Dados() =>
        Json(await painel.AuditoriaAsync(DataTablesRequest.From(Request.Query), HttpContext.RequestAborted));
}
