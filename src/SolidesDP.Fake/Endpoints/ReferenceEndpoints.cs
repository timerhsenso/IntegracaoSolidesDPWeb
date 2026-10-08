using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Services;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Endpoints;

/// <summary>Endpoints de dados de referencia: companies, job-role, workplace, work-schedule e punch-rule.</summary>
internal static class ReferenceEndpoints
{
    public static void MapReferenceEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapApi("GET", "/test", ErrorShape.Spring, _ => Results.Text("OK"));

        MapCompanies(routes);
        MapJobRoles(routes);
        MapWorkplaces(routes);
        MapSchedulesAndRules(routes);
    }

    private static void MapCompanies(IEndpointRouteBuilder routes)
    {
        routes.MapApi("GET", "/companies", ErrorShape.Spring, request =>
        {
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => page.ToPage([.. d.Companies.Values], c => c.ToNode())));
        });

        routes.MapApi("POST", "/companies", ErrorShape.Spring, async request =>
        {
            var body = await request.ReadJsonAsync("CompanyCNPJDTO");
            var cnpj = new string((body.GetString("cnpj") ?? body.GetString("cnpjMask") ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (cnpj.Length == 0)
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'cnpj' is required"));
            }

            if (cnpj.Length != 14)
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.InvalidValue, $"Invalid 'cnpj': must have 14 digits (got {cnpj.Length})"));
            }

            var created = request.Store.Execute(d =>
            {
                if (d.Companies.Values.Any(c => c.Cnpj == cnpj))
                {
                    throw new ApiException(ApiError.Conflict(ErrorCodes.DuplicateCnpj, $"A company with CNPJ {cnpj} already exists"));
                }

                var company = new CompanyRecord
                {
                    Id = d.NextId(EntityKind.Company),
                    Cnpj = cnpj,
                    CnpjMask = $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}",
                    FantasyName = body.GetString("fantasyName"),
                    SocialReason = body.GetString("socialReason"),
                    DescriptionName = body.GetString("descriptionName") ?? body.GetString("fantasyName") ?? body.GetString("socialReason"),
                    ExternalId = body.GetString("externalId"),
                };
                d.Companies[company.Id] = company;
                return company.ToNode();
            });
            return ApiResults.Json(created);
        });
    }

    private static void MapJobRoles(IEndpointRouteBuilder routes)
    {
        routes.MapApi("POST", "/job-role/register", ErrorShape.Spring, async request =>
        {
            var body = await request.ReadJsonAsync("JobRoleDTO");
            var description = body.GetString("description");
            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'description' is required"));
            }

            var externalId = NullIfBlank(body.GetString("externalId"));
            var cbo = NullIfBlank(body.GetString("cbo"));
            var now = request.NowMillis;
            var behavior = request.Behavior;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var existing = externalId is null ? null : d.JobRoles.Values.FirstOrDefault(j => j.ExternalId == externalId);
                if (existing is not null)
                {
                    switch (behavior.JobRoleDuplicateExternalId)
                    {
                        case JobRoleDuplicateExternalIdMode.Error:
                            throw new ApiException(ApiError.Conflict(ErrorCodes.DuplicateExternalId, $"A job role with externalId '{externalId}' already exists"));
                        case JobRoleDuplicateExternalIdMode.Update:
                            var updated = existing with
                            {
                                Description = description,
                                Cbo = cbo ?? (behavior.UpdateOmittedFields == UpdateOmittedFieldsMode.Keep ? existing.Cbo : null),
                                AlterationDate = now,
                            };
                            d.JobRoles[updated.Id] = updated;
                            return updated.ToNode();
                        default:
                            break; // Duplicate: cria outro cargo com o mesmo externalId
                    }
                }

                var created = new JobRoleRecord
                {
                    Id = d.NextId(EntityKind.JobRole),
                    Description = description,
                    ExternalId = externalId,
                    Cbo = cbo,
                    AlterationDate = now,
                };
                d.JobRoles[created.Id] = created;
                return created.ToNode();
            }));
        });

        routes.MapApi("GET", "/job-role/find", ErrorShape.Spring, request =>
        {
            var (tangerinoId, externalId) = LookupKey(request);
            return ApiResults.Json(request.Store.Execute(d =>
            {
                var found = tangerinoId is { } id
                    ? d.JobRoles.GetValueOrDefault(id)
                    : d.JobRoles.Values.FirstOrDefault(j => j.ExternalId == externalId);
                return (found ?? throw new ApiException(ApiError.NotFound("Job role not found"))).ToNode();
            }));
        });

        routes.MapApi("GET", "/job-role/find-all", ErrorShape.Spring, request =>
        {
            var lastUpdate = request.QueryLong("lastUpdate");
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => page.ToPage(
                [.. d.JobRoles.Values.Where(j => lastUpdate is null || j.AlterationDate >= lastUpdate)],
                j => j.ToNode())));
        });
    }

    private static void MapWorkplaces(IEndpointRouteBuilder routes)
    {
        routes.MapApi("POST", "/workplace/register", ErrorShape.Spring, async request =>
        {
            var body = await request.ReadJsonAsync("WorkplaceDTO");
            var name = body.GetString("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'name' is required"));
            }

            var externalId = NullIfBlank(body.GetString("externalId"));
            var allowUpdate = request.QueryBool("allowUpdate") ?? false;
            var now = request.NowMillis;

            return ApiResults.Json(request.Store.Execute(d =>
            {
                var existing = externalId is null ? null : d.Workplaces.Values.FirstOrDefault(w => w.ExternalId == externalId);
                if (existing is not null)
                {
                    if (!allowUpdate)
                    {
                        throw new ApiException(ApiError.Conflict(ErrorCodes.AlreadyExists, $"A workplace with externalId '{externalId}' already exists; use allowUpdate=true to update it"));
                    }

                    var updated = existing with { Name = name, UpdatedAt = now };
                    d.Workplaces[updated.Id] = updated;
                    return Wire.Workplace(updated);
                }

                var created = new WorkplaceRecord { Id = d.NextId(EntityKind.Workplace), Name = name, ExternalId = externalId, UpdatedAt = now };
                d.Workplaces[created.Id] = created;
                return Wire.Workplace(created);
            }));
        });

        routes.MapApi("GET", "/workplace/find", ErrorShape.Spring, request =>
        {
            var (tangerinoId, externalId) = LookupKey(request);
            return ApiResults.Json(request.Store.Execute(d =>
            {
                var found = tangerinoId is { } id
                    ? d.Workplaces.GetValueOrDefault(id)
                    : d.Workplaces.Values.FirstOrDefault(w => w.ExternalId == externalId);
                return Wire.Workplace(found ?? throw new ApiException(ApiError.NotFound("Workplace not found")));
            }));
        });

        routes.MapApi("GET", "/workplace/find-all", ErrorShape.Spring, request =>
        {
            var active = request.QueryBool("active");
            var lastUpdate = request.QueryLong("lastUpdate");
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => page.ToPage(
                [.. d.Workplaces.Values.Where(w => (active is null || w.Active == active) && (lastUpdate is null || w.UpdatedAt >= lastUpdate))],
                w => Wire.Workplace(w))));
        });
    }

    private static void MapSchedulesAndRules(IEndpointRouteBuilder routes)
    {
        routes.MapApi("GET", "/work-schedule", ErrorShape.Spring, request =>
        {
            var active = request.QueryBool("active");
            var lastUpdate = request.QueryLong("lastUpdate");
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => page.ToPage(
                [.. d.WorkSchedules.Values.Where(s => (active is null || !s.Inactive == active) && (lastUpdate is null || s.AlterationDate >= lastUpdate))],
                s => s.ToNode())));
        });

        routes.MapApi("GET", "/work-schedule/default", ErrorShape.Spring, request =>
            ApiResults.Json(request.Store.Execute(d =>
                (d.WorkSchedules.Values.FirstOrDefault(s => s.Standard) ?? throw new ApiException(ApiError.NotFound("There is no standard work schedule"))).ToNode())));

        routes.MapApi("GET", "/v2/punch-rule", ErrorShape.Spring, request =>
        {
            var page = request.Page;
            return ApiResults.Json(request.Store.Execute(d => ApiResults.BaseItem(page.ToPage([.. d.PunchRules.Values], p => p.ToNode()))));
        });
    }

    private static (long? TangerinoId, string? ExternalId) LookupKey(ApiRequest request)
    {
        var tangerinoId = request.QueryLong("tangerinoId");
        var externalId = request.QueryString("externalId");
        return tangerinoId is null && externalId is null
            ? throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'externalId' or 'tangerinoId' is required"))
            : (tangerinoId, externalId);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
