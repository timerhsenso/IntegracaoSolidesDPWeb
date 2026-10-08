using Microsoft.AspNetCore.Http.Features;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Http;

/// <summary>Registra todo request nao-admin (com corpo) no diario; a entrada aparece antes da resposta chegar ao cliente.</summary>
internal sealed class JournalMiddleware(RequestDelegate next, RequestJournal journal)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (FakePaths.IsAdmin(http.Request.Path))
        {
            await next(http);
            return;
        }

        var body = await BodyReader.ReadTextAsync(http);
        var query = http.Request.QueryString.HasValue ? http.Request.QueryString.Value![1..] : null;
        var scope = journal.Begin(http.Request.Method, http.Request.Path.Value ?? "/", query, body);
        http.Items[RequestJournal.JournalScope.ItemKey] = scope;
        http.Response.OnStarting(
            static state =>
            {
                var (context, current) = ((HttpContext, RequestJournal.JournalScope))state;
                current.Complete(context.Response.StatusCode);
                return Task.CompletedTask;
            },
            (http, scope));

        try
        {
            await next(http);
        }
        finally
        {
            scope.Complete(http.Response.StatusCode);
        }
    }
}

/// <summary>Converte excecoes inesperadas em HTTP 500 no estilo Spring.</summary>
internal sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // cliente desistiu; nada a responder
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            logger.LogError(ex, "Unhandled error in {Method} {Path}", http.Request.Method, http.Request.Path);
            http.Response.Headers[ApiResults.ErrorCodeHeader] = "internal_error";
            await ApiResults.WriteSpringAsync(http, StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}

/// <summary>Autenticacao por token: <c>Authorization: Basic &lt;token&gt;</c> (ou o token puro se o prefixo nao for exigido).</summary>
internal sealed class AuthMiddleware(RequestDelegate next, FakeStore store)
{
    private const string BasicPrefix = "Basic ";

    public async Task InvokeAsync(HttpContext http)
    {
        if (FakePaths.IsAdmin(http.Request.Path) || IsAuthorized(http.Request.Headers.Authorization.ToString(), store.Behavior.RequireBasicPrefix))
        {
            await next(http);
            return;
        }

        http.Response.Headers[ApiResults.ErrorCodeHeader] = "unauthorized";
        await ApiResults.WriteSpringAsync(http, StatusCodes.Status401Unauthorized, "Full authentication is required to access this resource");
    }

    private bool IsAuthorized(string header, bool requireBasicPrefix)
    {
        header = header.Trim();
        string token;
        if (header.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = header[BasicPrefix.Length..].Trim();
        }
        else if (!requireBasicPrefix)
        {
            token = header;
        }
        else
        {
            return false;
        }

        return token.Length > 0 && store.Tokens.Contains(token);
    }
}

/// <summary>Aplica as <see cref="FaultRule"/> ativas (depois da autenticacao: 401 nao consome falhas).</summary>
internal sealed class FaultMiddleware(RequestDelegate next, FaultRegistry faults, FakeStore store, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var rule = FakePaths.IsAdmin(http.Request.Path) ? null : faults.TryConsume(http.Request.Method, http.Request.Path.Value ?? "/");
        if (rule is null)
        {
            await next(http);
            return;
        }

        var scope = http.Items[RequestJournal.JournalScope.ItemKey] as RequestJournal.JournalScope;
        scope?.Fault = rule.Kind.ToString();

        switch (rule.Kind)
        {
            case FaultKind.Status:
                AddRetryAfter(http, rule);
                var status = rule.Status ?? StatusCodes.Status500InternalServerError;
                http.Response.Headers[ApiResults.ErrorCodeHeader] = ErrorCodes.InjectedFault;
                await ApiResults.WriteSpringAsync(http, status, rule.Message ?? $"Injected fault: HTTP {status}");
                break;

            case FaultKind.Delay:
                await Task.Delay(TimeSpan.FromMilliseconds(rule.DelayMs ?? 1000), time, http.RequestAborted);
                await next(http);
                break;

            case FaultKind.ErrorInBody:
                AddRetryAfter(http, rule);
                var shape = http.GetEndpoint()?.Metadata.GetMetadata<ApiEndpointMetadata>()?.Shape ?? ErrorShape.Spring;
                var error = new ApiError(ErrorCodes.InjectedFault, rule.Message ?? "Injected fault", rule.Status ?? StatusCodes.Status400BadRequest);
                await ApiResults.Error(http, error, shape, store.Behavior.ErrorStyle).ExecuteAsync(http);
                break;

            case FaultKind.CommitThenDrop:
                // Tudo que o handler escrever vai para o nada: o cliente nunca ve a resposta, mas a operacao e persistida.
                http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
                await next(http);
                scope?.Complete(http.Response.StatusCode);

                // Kestrel: a conexao e resetada (cliente: HttpRequestException). TestServer (em processo): o cliente recebe
                // OperationCanceledException("The application aborted the request.").
                http.Abort();
                break;

            default:
                await next(http);
                break;
        }
    }

    private static void AddRetryAfter(HttpContext http, FaultRule rule)
    {
        if (rule.RetryAfterSeconds is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
