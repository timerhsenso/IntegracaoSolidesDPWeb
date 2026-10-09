using System.Text.Json;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

[ExigeGestaoPreparada]
public sealed class ExecucoesController(PainelRepository painel, EmpresasRepository empresas) : Controller
{
    public async Task<IActionResult> Index() => View(await empresas.EmpresasAtivasAsync(HttpContext.RequestAborted));

    [HttpGet]
    public async Task<IActionResult> Dados(int? empresa) =>
        Json(await painel.ExecucoesAsync(DataTablesRequest.From(Request.Query), HttpContext.RequestAborted, empresa));

    public async Task<IActionResult> Detalhe(Guid id)
    {
        var execucao = await painel.ExecucaoAsync(id, HttpContext.RequestAborted);
        if (execucao is null)
        {
            return NotFound();
        }

        return View(new ExecucaoDetalheViewModel { Execucao = execucao, Contagens = Contagens(execucao.ResumoJson) });
    }

    [HttpGet]
    public async Task<IActionResult> Itens(Guid id, string? entidade, string? status, bool pendencias = false) =>
        Json(await painel.ItensAsync(id, entidade, status, pendencias, DataTablesRequest.From(Request.Query), HttpContext.RequestAborted));

    /// <summary>summary_json: {"employee": {"created": 3, "unchanged": 120}, ...}.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Contagens(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, IReadOnlyDictionary<string, int>>();
        }

        try
        {
            var lido = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json)
                       ?? new Dictionary<string, Dictionary<string, int>>();
            return lido.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, int>)p.Value, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, int>>();
        }
    }
}
