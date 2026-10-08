using System.Net;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Senha definida pelo administrador: só libera o sistema depois da troca.</summary>
public sealed class TrocaSenhaFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        var controller = context.RouteData.Values["controller"] as string;
        if (user.Identity?.IsAuthenticated == true
            && user.HasClaim(ClaimsWeb.TrocaSenha, "1")
            && !string.Equals(controller, "Conta", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new RedirectToActionResult("AlterarSenha", "Conta", null);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>Telas que leem as tabelas do serviço: sem elas, volta ao painel (que explica o que falta).</summary>
public sealed class ExigeGestaoPreparadaAttribute : TypeFilterAttribute
{
    public ExigeGestaoPreparadaAttribute()
        : base(typeof(GestaoPreparadaFilter))
    {
    }
}

public sealed class GestaoPreparadaFilter(PainelRepository painel) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await painel.GestaoPreparadaAsync(context.HttpContext.RequestAborted))
        {
            context.Result = new RedirectToActionResult("Index", "Painel", null);
            return;
        }

        await next();
    }
}

public static class HttpContextExtensions
{
    /// <summary>Requisição feita no próprio servidor (navegador aberto no servidor).</summary>
    public static bool IsLocal(this HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && (IPAddress.IsLoopback(remote) || remote.Equals(context.Connection.LocalIpAddress));
    }

    /// <summary>Login do usuário logado, para auditoria e "solicitado_por".</summary>
    public static string Usuario(this HttpContext context) => context.User.Identity?.Name ?? "?";
}
