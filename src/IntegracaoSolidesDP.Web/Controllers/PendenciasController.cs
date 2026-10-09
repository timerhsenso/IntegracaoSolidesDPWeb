using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>Itens da última execução que pedem ação do RH (falhas, bloqueios, ignorados, avisos, adiados).</summary>
[ExigeGestaoPreparada]
public sealed class PendenciasController(PainelRepository painel, EmpresasRepository empresas) : Controller
{
    /// <summary>Última execução da empresa escolhida (sem empresa: a última de qualquer empresa).</summary>
    public async Task<IActionResult> Index(int? empresa)
    {
        var ct = HttpContext.RequestAborted;
        return View(new PendenciasViewModel
        {
            Execucao = await painel.UltimaExecucaoAsync(somenteReal: false, ct, empresa),
            Empresas = await empresas.EmpresasAtivasAsync(ct),
            Empresa = empresa,
        });
    }
}
