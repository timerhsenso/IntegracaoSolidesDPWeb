using System.Globalization;
using System.Text.Json.Nodes;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Services;

namespace SolidesDP.Fake.Endpoints;

/// <summary>Endpoints de motivos de lancamento e lancamentos (ferias, abono, atestado...).</summary>
internal static class AdjustmentEndpoints
{
    public static void MapAdjustmentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapApi("GET", "/adjustment-reason/find-all", ErrorShape.Spring, request =>
        {
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => page.ToPage([.. d.AdjustmentReasons.Values], r => r.ToNode())));
        });

        routes.MapApi("POST", "/adjustment/register", ErrorShape.Launch, request => RegisterAsync(request, "AdjustmentReasonRecordDTO", nestedDtos: true));
        routes.MapApi("POST", "/adjustment/register/1.1", ErrorShape.Launch, request => RegisterAsync(request, "AdjustmentReasonRegisterDTO", nestedDtos: false));

        routes.MapApi("GET", "/adjustment/find-all", ErrorShape.Spring, request =>
        {
            var employeeId = request.QueryLong("employeeId");
            var reasonId = request.QueryLong("adjustmentReasonId");
            var workplaceId = request.QueryLong("workplaceId");
            var lastUpdate = request.QueryLong("lastUpdate");
            var ignoreExcluded = request.QueryBool("ignoreExcluded") ?? false;
            var page = request.Page;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var adjustments = d.Adjustments.Values
                    .Where(a => !(ignoreExcluded && a.Excluded))
                    .Where(a => employeeId is null || a.EmployeeId == employeeId)
                    .Where(a => reasonId is null || a.AdjustmentReasonId == reasonId)
                    .Where(a => lastUpdate is null || a.LastUpdate >= lastUpdate)
                    .Where(a => workplaceId is null || (d.Employees.TryGetValue(a.EmployeeId, out var e) && e.WorkplaceId == workplaceId))
                    .ToList();
                return page.ToPage(adjustments, a => Wire.AdjustmentResponse(a, d));
            }));
        });

        routes.MapApi("GET", "/adjustment/{id}", ErrorShape.Spring, request =>
        {
            var id = RouteId(request);
            return ApiResults.Json(request.Store.Execute(d =>
            {
                var adjustment = d.Adjustments.GetValueOrDefault(id) ?? throw new ApiException(ApiError.NotFound($"Adjustment {id} not found"));
                return Wire.AdjustmentResponse(adjustment, d);
            }));
        });

        routes.MapApi("PUT", "/adjustment/update/{id}", ErrorShape.Launch, async request =>
        {
            var id = RouteId(request);
            var body = await request.ReadJsonAsync("AdjustmentReasonRecordUpdateV2DTO");
            if (body.GetLong("adjustmenteReasonRecordId") is { } bodyId && bodyId != id)
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.InvalidValue, $"'adjustmenteReasonRecordId' ({bodyId}) does not match the path id ({id})"));
            }

            var patch = new AdjustmentPatch
            {
                EmployeeId = body.GetLong("employeeId"),
                ReasonId = body.GetLong("adjustmentReasonId"),
                StartDate = body.GetLong("startDate"),
                EndDate = body.GetLong("endDate"),
                FullDay = body.GetBool("fullDay"),
                Status = body.GetString("status"),
                Observation = body.GetString("observation"),
                Origem = body.GetString("origem"),
                FirstDayIsPartial = body.GetBool("firstDayIsPartial"),
                Excluded = body.GetBool("excluded"),
            };
            var now = request.NowMillis;
            var behavior = request.Behavior;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var updated = AdjustmentService.Update(d, id, patch, behavior, now);
                return new JsonObject
                {
                    ["registered"] = true,
                    ["message"] = "Adjustment updated successfully",
                    ["adjustmentReasonDTO"] = d.AdjustmentReasons[updated.AdjustmentReasonId].ToNode(),
                    ["entity"] = Wire.AdjustmentRecordDto(updated, d),
                };
            }));
        });

        routes.MapApi("PUT", "/adjustment/{id}", ErrorShape.ResponseEntity, async request =>
        {
            var id = RouteId(request);
            var body = await request.ReadJsonAsync("AdjustmentReasonRecordUpdateDTO");
            var patch = new AdjustmentPatch
            {
                StartDate = body.GetLong("startDate"),
                EndDate = body.GetLong("endDate"),
                FullDay = body.GetBool("fullDay"),
                Status = body.GetString("status"),
                Origem = body.GetString("origem"),
            };
            var now = request.NowMillis;
            var behavior = request.Behavior;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var updated = AdjustmentService.Update(d, id, patch, behavior, now);
                return ApiResults.ResponseEntity(Wire.AdjustmentResponse(updated, d), 200);
            }));
        });
    }

    private static async Task<IResult> RegisterAsync(ApiRequest request, string definition, bool nestedDtos)
    {
        var body = await request.ReadJsonAsync(definition);
        var input = AdjustmentService.ReadInput(body, nestedDtos);
        var now = request.NowMillis;
        var behavior = request.Behavior;

        return ApiResults.Json(request.Store.Execute(d =>
        {
            var adjustment = AdjustmentService.Register(d, input, behavior, now);
            return new JsonObject
            {
                ["registered"] = true,
                ["message"] = "Adjustment registered successfully",
                ["adjustmentReasonDTO"] = d.AdjustmentReasons[adjustment.AdjustmentReasonId].ToNode(),
                ["entity"] = Wire.AdjustmentResponse(adjustment, d),
            };
        }));
    }

    private static long RouteId(ApiRequest request)
    {
        var raw = Convert.ToString(request.Http.Request.RouteValues["id"], CultureInfo.InvariantCulture);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new ApiException(ApiError.TransportFailure(
                ErrorCodes.InvalidValue,
                $"Failed to convert value of type 'java.lang.String' to required type 'java.lang.Long'; For input string: \"{raw}\""));
    }
}
