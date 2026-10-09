using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

public sealed class PainelController(PainelRepository painel, GestaoService gestao, Formatador formatador, TimeProvider clock) : Controller
{
    private const int Dias = 14;

    public async Task<IActionResult> Index()
    {
        var ct = HttpContext.RequestAborted;
        if (!await painel.GestaoPreparadaAsync(ct))
        {
            return View(new PainelViewModel { GestaoPreparada = false });
        }

        var configuracao = await gestao.ConfiguracaoAtualAsync(ct);
        var ultimaReal = await painel.UltimaExecucaoAsync(somenteReal: true, ct);
        var hoje = formatador.Hoje(clock);
        var primeiroDia = hoje.AddDays(-(Dias - 1));
        var execucoes = await painel.ExecucoesDesdeAsync(clock.GetUtcNow().AddDays(-(Dias + 1)), ct);
        var aguardandoDesde = await painel.PedidoMaisAntigoAguardandoAsync(ct);

        return View(new PainelViewModel
        {
            GestaoPreparada = true,
            Configuracao = configuracao,
            EmAndamento = await painel.ExecucaoEmAndamentoAsync(ct),
            UltimaExecucao = await painel.UltimaExecucaoAsync(somenteReal: false, ct),
            UltimaExecucaoReal = ultimaReal,
            PendenciasUltimaReal = ultimaReal is null ? 0 : await painel.PendenciasAsync(ultimaReal.RunId, ct),
            Totais = await painel.TotaisAsync(ct),
            PorDia = Enumerable.Range(0, Dias)
                .Select(i => primeiroDia.AddDays(i))
                .Select(dia =>
                {
                    var doDia = execucoes.Where(e => formatador.DiaLocal(e.Inicio) == dia).ToList();
                    return new ExecucoesDoDia(
                        dia,
                        doDia.Count(e => e.Status == RunStatuses.Completed),
                        doDia.Count(e => e.Status == RunStatuses.CompletedWithErrors),
                        doDia.Count(e => e.Status == RunStatuses.Failed));
                })
                .ToList(),
            ComandosRecentes = await painel.ComandosRecentesAsync(8, ct),
            PedidoParadoDesde = aguardandoDesde is { } desde && clock.GetUtcNow() - desde > TimeSpan.FromMinutes(2) ? desde : null,
        });
    }
}
