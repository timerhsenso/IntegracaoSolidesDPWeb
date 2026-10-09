using IntegracaoSolidesDP.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

/// <summary>
/// Manual de uso (todos os perfis) e o manual técnico em PDF (só Admin). Não depende das tabelas
/// do serviço: a ajuda abre mesmo antes de a gestão estar preparada.
/// </summary>
public sealed class AjudaController : Controller
{
    /// <summary>Nome do recurso embutido (docs/manual-tecnico/manual-tecnico.pdf, ver o .csproj).</summary>
    public const string RecursoManualTecnico = "IntegracaoSolidesDP.Web.ManualTecnico.pdf";

    public const string ArquivoManualTecnico = "IntegracaoSolidesDP-manual-tecnico.pdf";

    public IActionResult Index() => View();

    /// <summary>
    /// O PDF vai embutido na dll, e não em wwwroot, para não ficar acessível sem login: traz
    /// nomes de chaves, comandos e caminhos do servidor.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Politicas.Administrar)]
    public IActionResult ManualTecnico()
    {
        var pdf = typeof(AjudaController).Assembly.GetManifestResourceStream(RecursoManualTecnico);
        return pdf is null ? NotFound() : File(pdf, "application/pdf", ArquivoManualTecnico);
    }
}
