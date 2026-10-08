using System.Text.Json;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>Regras da seção Sync, versionadas em solidesdp.configuracao. Cada gravação é uma versão nova.</summary>
[ExigeGestaoPreparada]
public sealed class ConfiguracaoController(GestaoService gestao, PainelRepository painel) : Controller
{
    private const int TamanhoMotivo = 500;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var atual = await gestao.ConfiguracaoAtualAsync(HttpContext.RequestAborted);
        var regras = new SyncOptions();
        if (atual is not null)
        {
            try
            {
                regras = SyncOptionsJson.Deserialize(atual.SyncJson);
            }
            catch (JsonException)
            {
                TempData["Erro"] = $"A versão {atual.Version} não pôde ser lida; o formulário mostra os valores padrão.";
            }
        }

        return View(new ConfiguracaoViewModel
        {
            Atual = atual,
            Form = ConfiguracaoForm.De(regras),
            PodeEditar = User.IsInRole(Perfis.Admin),
            GestaoPreparada = true,
        });
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public async Task<IActionResult> Salvar([Bind(Prefix = "Form")] ConfiguracaoForm form)
    {
        var ct = HttpContext.RequestAborted;
        var erros = new Dictionary<string, string>(StringComparer.Ordinal);
        var regras = form.ParaOpcoes(erros);
        foreach (var (campo, mensagem) in erros)
        {
            ModelState.AddModelError($"Form.{campo}", mensagem);
        }

        if (ModelState.IsValid)
        {
            var resultado = await gestao.SalvarConfiguracaoAsync(regras, form.Observacao.Trim(), HttpContext.Usuario(), ct);
            if (resultado.Sucesso)
            {
                TempData["Sucesso"] = $"Configuração salva (versão {resultado.Id}). O serviço passa a usá-la na próxima execução.";
                return RedirectToAction(nameof(Index));
            }

            foreach (var erro in resultado.Erros)
            {
                ModelState.AddModelError(string.Empty, erro);
            }
        }

        return View(nameof(Index), new ConfiguracaoViewModel
        {
            Atual = await gestao.ConfiguracaoAtualAsync(ct),
            Form = form,
            PodeEditar = true,
            GestaoPreparada = true,
        });
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public Task<IActionResult> Ativar(string? motivo) => DefinirAtivaAsync(true, motivo);

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public Task<IActionResult> Desativar(string? motivo) => DefinirAtivaAsync(false, motivo);

    [HttpGet]
    public async Task<IActionResult> Historico() => View(await painel.VersoesAsync(HttpContext.RequestAborted));

    [HttpGet]
    public async Task<IActionResult> Versao(int id)
    {
        var versoes = await painel.VersoesAsync(HttpContext.RequestAborted);
        var versao = versoes.FirstOrDefault(v => v.Versao == id);
        if (versao is null)
        {
            return NotFound();
        }

        var anterior = versoes.Where(v => v.Versao < id).MaxBy(v => v.Versao);
        return View(new VersaoViewModel { Versao = versao, Anterior = anterior, Diferencas = VersaoViewModel.Comparar(versao, anterior) });
    }

    private async Task<IActionResult> DefinirAtivaAsync(bool ativa, string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > TamanhoMotivo)
        {
            TempData["Erro"] = $"Informe o motivo (até {TamanhoMotivo} caracteres).";
            return RedirectToAction("Index", "Painel");
        }

        var resultado = await gestao.DefinirAtivaAsync(ativa, motivo.Trim(), HttpContext.Usuario(), HttpContext.RequestAborted);
        if (resultado.Sucesso)
        {
            TempData["Sucesso"] = ativa
                ? $"Integração ativada (versão {resultado.Id})."
                : $"Integração desativada (versão {resultado.Id}). O serviço não fará execuções reais até ser ativada de novo.";
        }
        else
        {
            TempData["Erro"] = string.Join(" ", resultado.Erros);
        }

        return RedirectToAction("Index", "Painel");
    }
}
