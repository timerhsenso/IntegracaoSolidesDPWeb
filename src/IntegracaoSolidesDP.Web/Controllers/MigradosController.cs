using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>O que já existe no Sólides DP por obra da integração (solidesdp.entity_state e vacation_state).</summary>
[ExigeGestaoPreparada]
public sealed class MigradosController(PainelRepository painel) : Controller
{
    public static readonly IReadOnlyList<string> Tipos = [EntityTypes.Employee, EntityTypes.JobRole, EntityTypes.Workplace, EntityTypes.Vacation];

    public IActionResult Index(string? tipo) =>
        View(new MigradosViewModel(tipo is not null && Tipos.Contains(tipo) ? tipo : EntityTypes.Employee));

    [HttpGet]
    public async Task<IActionResult> Dados(string tipo)
    {
        var request = DataTablesRequest.From(Request.Query);
        var ct = HttpContext.RequestAborted;
        if (tipo == EntityTypes.Vacation)
        {
            return Json(await painel.FeriasAsync(request, ct));
        }

        if (!Tipos.Contains(tipo))
        {
            return BadRequest();
        }

        return Json(await painel.MigradosAsync(tipo, request, ct));
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
