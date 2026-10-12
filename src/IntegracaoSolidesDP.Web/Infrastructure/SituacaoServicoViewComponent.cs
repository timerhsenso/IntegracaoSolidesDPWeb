using System.Text.Json;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>
/// Se o serviço está respondendo, pelo sinal de vida (solidesdp.servico_sinal). Completo no Painel (lista os serviços);
/// compacto nas telas de pedidos (só o aviso quando nenhum serviço responde).
/// </summary>
public sealed class SituacaoServicoViewComponent(ISinalServicoStore sinais, GestaoService gestao, TimeProvider clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(bool compacto)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var servicos = await sinais.ListarAsync(ct);
            var minutos = await LimiteMinutosAsync(ct);
            return View(new SituacaoServicoModel(servicos, TimeSpan.FromMinutes(minutos), clock.GetUtcNow(), compacto));
        }
        catch (SqlException)
        {
            // Banco fora do ar ou tabelas ainda não criadas: a própria tela já mostra o problema.
            return Content(string.Empty);
        }
    }

    /// <summary>Sync:ServicoSemRespostaMinutos da configuração em vigor (padrão se ainda não há configuração).</summary>
    private async Task<int> LimiteMinutosAsync(CancellationToken ct)
    {
        var padrao = new SyncOptions().ServicoSemRespostaMinutos;
        if (await gestao.ConfiguracaoAtualAsync(ct) is not { } atual)
        {
            return padrao;
        }

        try
        {
            return SyncOptionsJson.Deserialize(atual.SyncJson).ServicoSemRespostaMinutos;
        }
        catch (JsonException)
        {
            return padrao;
        }
    }
}
