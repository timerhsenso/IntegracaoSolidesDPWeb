namespace SolidesDP.Fake.Http;

/// <summary>Metadado de endpoint: o formato de erro que o endpoint usa (lido pelas falhas injetadas).</summary>
internal sealed record ApiEndpointMetadata(ErrorShape Shape);

/// <summary>Registro de rotas da API do fornecedor: captura <see cref="ApiException"/> e a renderiza no formato do endpoint.</summary>
internal static class ApiRoutes
{
    public static void MapApi(this IEndpointRouteBuilder routes, string method, string pattern, ErrorShape shape, Func<ApiRequest, Task<IResult>> handler) =>
        routes.MapMethods(pattern, [method], async (HttpContext http) =>
        {
            var request = new ApiRequest(http);
            try
            {
                return await handler(request);
            }
            catch (ApiException ex)
            {
                return ApiResults.Error(http, ex.Error, shape, request.Behavior.ErrorStyle);
            }
        }).WithMetadata(new ApiEndpointMetadata(shape));

    public static void MapApi(this IEndpointRouteBuilder routes, string method, string pattern, ErrorShape shape, Func<ApiRequest, IResult> handler) =>
        routes.MapApi(method, pattern, shape, request => Task.FromResult(handler(request)));
}
