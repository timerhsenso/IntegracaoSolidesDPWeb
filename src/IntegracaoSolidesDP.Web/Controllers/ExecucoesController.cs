using System.Text;
using System.Text.Json;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

[ExigeGestaoPreparada]
public sealed class ExecucoesController(PainelRepository painel, EmpresasRepository empresas, Auditoria auditoria, VersoesSistema versoes) : Controller
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

        return View(new ExecucaoDetalheViewModel
        {
            Execucao = execucao,
            Contagens = Contagens(execucao.ResumoJson),
            Relatorios = await painel.RelatoriosAsync(id, HttpContext.RequestAborted),
            VersaoServico = await versoes.ServicoDaExecucaoAsync(id, HttpContext.RequestAborted),
        });
    }

    /// <summary>Baixa um relatório da execução (CSV com BOM, para o Excel abrir a acentuação). Fica na auditoria.</summary>
    [HttpGet]
    public async Task<IActionResult> Relatorio(Guid id, string tipo)
    {
        var relatorio = await painel.RelatorioAsync(id, tipo, HttpContext.RequestAborted);
        if (relatorio is null)
        {
            return NotFound();
        }

        await auditoria.RegistrarAsync(AcoesAuditoria.RelatorioBaixado, $"{relatorio.Arquivo} (execução {id})", ct: HttpContext.RequestAborted);
        var conteudo = Encoding.UTF8.GetBytes(relatorio.Conteudo);
        var bytes = new byte[Encoding.UTF8.Preamble.Length + conteudo.Length];
        Encoding.UTF8.Preamble.CopyTo(bytes);
        conteudo.CopyTo(bytes, Encoding.UTF8.Preamble.Length);
        return File(bytes, "text/csv; charset=utf-8", relatorio.Arquivo);
    }

    /// <summary>
    /// Linhas do relatório de vinculação (JSON gravado pelo serviço em toda simulação) para a grade do detalhe.
    /// Ver na tela não fica na auditoria; baixar o CSV fica.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Vinculacao(Guid id)
    {
        var relatorio = await painel.RelatorioAsync(id, TiposRelatorio.VinculacaoLinhas, HttpContext.RequestAborted);
        return relatorio is null ? NotFound() : Content(relatorio.Conteudo, "application/json; charset=utf-8");
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
