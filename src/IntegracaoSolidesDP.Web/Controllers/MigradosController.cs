using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>O que está vinculado ao Sólides DP, por empresa (solidesdp.colaborador_vinculo, cargo_vinculo, local_vinculo e ferias_vinculo).</summary>
[ExigeGestaoPreparada]
public sealed class MigradosController(PainelRepository painel, EmpresasRepository empresas) : Controller
{
    public static readonly IReadOnlyList<string> Tipos = [EntityTypes.Employee, EntityTypes.JobRole, EntityTypes.Workplace, EntityTypes.Vacation];

    public async Task<IActionResult> Index(string? tipo) =>
        View(new MigradosViewModel(tipo is not null && Tipos.Contains(tipo) ? tipo : EntityTypes.Employee)
        {
            Empresas = await empresas.EmpresasAtivasAsync(HttpContext.RequestAborted),
        });

    [HttpGet]
    public async Task<IActionResult> Dados(string tipo, int? empresa)
    {
        var request = DataTablesRequest.From(Request.Query);
        var ct = HttpContext.RequestAborted;
        if (tipo == EntityTypes.Vacation)
        {
            return Json(await painel.FeriasAsync(empresa, request, ct));
        }

        if (!Tipos.Contains(tipo))
        {
            return BadRequest();
        }

        return Json(await painel.MigradosAsync(tipo, empresa, request, ct));
    }

    public async Task<IActionResult> Historico(string tipo, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest();
        }

        var itens = await painel.HistoricoAsync(id.Trim(), HttpContext.RequestAborted);
        return View(new HistoricoViewModel { Tipo = tipo, ExternalId = id.Trim(), Itens = itens });
    }
}
