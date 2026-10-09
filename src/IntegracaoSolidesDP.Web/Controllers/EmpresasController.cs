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

/// <summary>
/// Empresas do RHSenso na integração. Cada empresa é uma conta do Sólides DP, com o seu token (cifrado no banco,
/// nunca exibido), as filiais que entram e as regras próprias. Cada gravação é uma versão nova da configuração.
/// </summary>
[ExigeGestaoPreparada]
public sealed class EmpresasController(
    EmpresasRepository empresas,
    PainelRepository painel,
    GestaoService gestao) : Controller
{
    private const int TamanhoMotivo = 500;

    public async Task<IActionResult> Index()
    {
        var ct = HttpContext.RequestAborted;
        var atual = await gestao.ConfiguracaoAtualAsync(ct);
        var geral = Regras(atual);
        var ativas = await empresas.EmpresasAtivasAsync(ct);
        var tokens = await gestao.TokensAsync(ct);
        var vinculos = await empresas.VinculosAsync(ct);
        var ultimas = (await painel.UltimasPorEmpresaAsync(ct)).ToDictionary(e => e.Cdempresa!.Value);
        var configuradas = atual?.Empresas.ToDictionary(e => e.Cdempresa) ?? [];

        var linhas = new List<LinhaEmpresa>();
        foreach (var empresa in ativas)
        {
            linhas.Add(Linha(empresa.Cdempresa, empresa.Nome, ativa: true));
        }

        // Configurada aqui, mas inativada na folha depois: continua aparecendo, fora do escopo.
        foreach (var codigo in configuradas.Keys.Except(ativas.Select(a => a.Cdempresa)).Order())
        {
            linhas.Add(Linha(codigo, await empresas.NomeAsync(codigo, ct), ativa: false));
        }

        return View(new EmpresasViewModel
        {
            Linhas = linhas,
            Configuracao = atual,
            DryRunGeral = geral.DryRun,
            EmpresasIncluidas = geral.EmpresasIncluidas.ToList(),
            PodeEditar = User.IsInRole(Perfis.Admin),
        });

        LinhaEmpresa Linha(int codigo, string? nome, bool ativa) => new()
        {
            Cdempresa = codigo,
            Nome = nome,
            AtivaNaFolha = ativa,
            Config = configuradas.GetValueOrDefault(codigo),
            Token = tokens.GetValueOrDefault(codigo),
            Vinculos = vinculos.GetValueOrDefault(codigo),
            UltimaExecucao = ultimas.GetValueOrDefault(codigo),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Editar(int id)
    {
        var ct = HttpContext.RequestAborted;
        var atual = await gestao.ConfiguracaoAtualAsync(ct);
        var config = atual?.Empresas.FirstOrDefault(e => e.Cdempresa == id);
        var modelo = await MontarAsync(id, EmpresaForm.De(id, config), atual, ct);
        return modelo is null ? NotFound() : View(modelo);
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public async Task<IActionResult> Salvar([Bind(Prefix = "Form")] EmpresaForm form)
    {
        var ct = HttpContext.RequestAborted;
        var atual = await gestao.ConfiguracaoAtualAsync(ct);
        var modelo = await MontarAsync(form.Cdempresa, form, atual, ct);
        if (modelo is null)
        {
            return NotFound();
        }

        if (!modelo.AtivaNaFolha && form.Habilitada)
        {
            ModelState.AddModelError("Form.Habilitada", "A empresa está inativa no RHSenso (temp1.flativo): ative-a na folha antes de habilitar.");
        }

        var disponiveis = modelo.FiliaisDisponiveis.Select(f => f.Cdfilial).ToHashSet();
        var invalidas = form.Filiais.Where(f => !disponiveis.Contains(f)).ToList();
        if (invalidas.Count > 0)
        {
            ModelState.AddModelError("Form.Filiais", $"Filiais que não estão ativas no RHSenso: {string.Join(", ", invalidas)}.");
        }

        if (ModelState.IsValid)
        {
            var resultado = await gestao.SalvarEmpresaAsync(form.ParaConfiguracao(), form.Observacao.Trim(), HttpContext.Usuario(), ct);
            if (resultado.Sucesso)
            {
                TempData["Sucesso"] = $"Empresa {form.Cdempresa} salva (configuração versão {resultado.Id}). O serviço passa a usá-la na próxima execução.";
                return RedirectToAction(nameof(Editar), new { id = form.Cdempresa });
            }

            foreach (var erro in resultado.Erros)
            {
                ModelState.AddModelError(string.Empty, erro);
            }
        }

        return View(nameof(Editar), modelo);
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public async Task<IActionResult> Token(int id, string? token)
    {
        var resultado = await gestao.SalvarTokenAsync(id, token, HttpContext.Usuario(), HttpContext.RequestAborted);
        if (resultado.Sucesso)
        {
            TempData["Sucesso"] = "Token gravado (cifrado). Use \"Testar o token\" para conferir se o Sólides DP o aceita.";
        }
        else
        {
            TempData["Erro"] = string.Join(" ", resultado.Erros);
        }

        return RedirectToAction(nameof(Editar), new { id });
    }

    [HttpPost]
    [Authorize(Policy = Politicas.Administrar)]
    public async Task<IActionResult> RemoverToken(int id, string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length > TamanhoMotivo)
        {
            TempData["Erro"] = $"Informe o motivo (até {TamanhoMotivo} caracteres).";
            return RedirectToAction(nameof(Editar), new { id });
        }

        await gestao.RemoverTokenAsync(id, motivo.Trim(), HttpContext.Usuario(), HttpContext.RequestAborted);
        TempData["Sucesso"] = "Token removido. Sem token, a empresa não faz execução real.";
        return RedirectToAction(nameof(Editar), new { id });
    }

    /// <summary>Pedido ao serviço só desta empresa (simular, executar, testar o token, consultar o Sólides DP...).</summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Operar)]
    public async Task<IActionResult> Solicitar(int id, string tipo)
    {
        var resultado = await gestao.SolicitarComandoAsync(tipo, HttpContext.Usuario(), id, HttpContext.RequestAborted);
        if (resultado.Sucesso)
        {
            TempData["Sucesso"] = $"Pedido \"{Rotulos.Comando(tipo)}\" da empresa {id} registrado. O serviço o atende em alguns segundos.";
            return RedirectToAction("Detalhe", "Comandos", new { id = resultado.Id });
        }

        TempData["Erro"] = string.Join(" ", resultado.Erros);
        return RedirectToAction(nameof(Editar), new { id });
    }

    private async Task<EmpresaEditarViewModel?> MontarAsync(int id, EmpresaForm form, ConfigurationVersion? atual, CancellationToken ct)
    {
        var ativas = await empresas.EmpresasAtivasAsync(ct);
        var ativa = ativas.FirstOrDefault(e => e.Cdempresa == id);
        var configurada = atual?.Empresas.Any(e => e.Cdempresa == id) == true;
        if (ativa is null && !configurada)
        {
            return null;
        }

        var geral = Regras(atual);
        var tokens = await gestao.TokensAsync(ct);
        form.Cdempresa = id;
        return new EmpresaEditarViewModel
        {
            Form = form,
            Nome = ativa?.Nome ?? await empresas.NomeAsync(id, ct),
            AtivaNaFolha = ativa is not null,
            FiliaisDisponiveis = await empresas.FiliaisAtivasAsync(id, ct),
            Token = tokens.GetValueOrDefault(id),
            Vinculos = (await empresas.VinculosAsync(ct)).GetValueOrDefault(id),
            UltimaExecucao = await painel.UltimaExecucaoAsync(somenteReal: false, ct, id),
            DryRunGeral = geral.DryRun,
            GoLiveGeral = geral.GoLiveDate,
            PodeEditar = User.IsInRole(Perfis.Admin),
            PodeOperar = User.IsInRole(Perfis.Operador) || User.IsInRole(Perfis.Admin),
        };
    }

    private static SyncOptions Regras(ConfigurationVersion? atual)
    {
        if (atual is null)
        {
            return new SyncOptions();
        }

        try
        {
            return SyncOptionsJson.Deserialize(atual.SyncJson);
        }
        catch (JsonException)
        {
            return new SyncOptions();
        }
    }
}
