using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntegracaoSolidesDP.Web.Controllers;

[AllowAnonymous]
public sealed class ErroController : Controller
{
    [Route("/Erro")]
    public IActionResult Index() => View();
}
