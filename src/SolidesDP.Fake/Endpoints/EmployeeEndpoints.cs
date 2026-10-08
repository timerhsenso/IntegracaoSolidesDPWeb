using System.Globalization;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Services;

namespace SolidesDP.Fake.Endpoints;

/// <summary>Endpoints de colaborador: register, find, find-all e dismiss.</summary>
internal static class EmployeeEndpoints
{
    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapApi("POST", "/employee/register", ErrorShape.ResponseEntity, async request =>
        {
            var body = await request.ReadJsonAsync("EmployeeDTO");
            var allowUpdate = request.QueryBool("allowUpdate") ?? false;
            _ = request.QueryBool("skipUnifiedSync"); // aceito (e validado); a sincronizacao unificada nao existe no fake
            var managerId = ReadManagerHeader(request);
            var now = request.NowMillis;
            var behavior = request.Behavior;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var outcome = EmployeeService.Register(d, body, allowUpdate, managerId, behavior, now);
                return ApiResults.ResponseEntity(Wire.Employee(outcome.Employee, d, includePin: true), outcome.Created ? 201 : 200);
            }));
        });

        routes.MapApi("GET", "/employee/find", ErrorShape.Spring, request =>
        {
            var tangerinoId = request.QueryLong("tangerinoId");
            var externalId = request.QueryString("externalId");
            if (tangerinoId is null && externalId is null)
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'externalId' or 'tangerinoId' is required"));
            }

            var ignoreFired = request.QueryBool("ignoreFired") ?? false;
            return ApiResults.Json(request.Store.Execute(d =>
            {
                var employee = EmployeeService.Find(d, tangerinoId, externalId, ignoreFired)
                    ?? throw new ApiException(ApiError.NotFound("Employee not found"));
                return Wire.Employee(employee, d, includePin: false);
            }));
        });

        routes.MapApi("GET", "/employee/find-all", ErrorShape.Spring, request =>
        {
            var showFired = (request.QueryLong("showFired") ?? 0) != 0;
            var lastUpdate = request.QueryLong("lastUpdate");
            var punchRuleId = request.QueryLong("fkPunchRule");
            var branchExternalId = request.QueryString("branchExternalId");
            var page = request.Page;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var employees = d.Employees.Values
                    .Where(e => showFired || !e.Fired)
                    .Where(e => lastUpdate is null || e.UpdatedAt >= lastUpdate)
                    .Where(e => punchRuleId is null || e.PunchRuleId == punchRuleId)
                    .Where(e => branchExternalId is null || (e.CompanyId is { } c && d.Companies.TryGetValue(c, out var company) && company.ExternalId == branchExternalId))
                    .ToList();
                return page.ToPage(employees, e => Wire.Employee(e, d, includePin: false));
            }));
        });

        routes.MapApi("POST", "/employee/dismiss", ErrorShape.ResponseEntity, async request =>
        {
            var body = await request.ReadJsonAsync("DismissDTO");
            var now = request.NowMillis;
            return ApiResults.Json(request.Store.Execute(d =>
            {
                var dismissed = EmployeeService.Dismiss(d, body, now);
                return ApiResults.ResponseEntity(Wire.Employee(dismissed, d, includePin: false), 200);
            }));
        });
    }

    private static long? ReadManagerHeader(ApiRequest request)
    {
        var raw = request.Http.Request.Headers["gestorId"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ApiException(ApiError.TransportFailure(ErrorCodes.InvalidValue, $"Failed to convert 'gestorId' header value '{raw}' to a number"));
    }
}
