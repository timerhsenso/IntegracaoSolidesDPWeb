using System.Text.Json.Nodes;
using SolidesDP.Fake.Domain;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Services;

/// <summary>Projecao dos registros internos para os formatos (DTOs) do Swagger.</summary>
internal static class Wire
{
    /// <summary><c>WorkplaceReturnDTO</c>.</summary>
    public static JsonObject Workplace(WorkplaceRecord w)
    {
        var node = new JsonObject { ["id"] = w.Id, ["name"] = w.Name, ["active"] = w.Active, ["standard"] = w.Standard };
        node.Put("externalId", w.ExternalId);
        return node;
    }

    /// <summary><c>CompanyReturnDTO</c> (a filial dentro do colaborador).</summary>
    public static JsonObject CompanyReturn(CompanyRecord c)
    {
        var node = new JsonObject { ["id"] = c.Id, ["accountStatus"] = "PAGANTE", ["standard"] = false };
        node.Put("descriptionName", c.DescriptionName);
        node.Put("externalId", c.ExternalId);
        node.Put("fantasyName", c.FantasyName);
        return node;
    }

    /// <summary><c>WorkScheduleDTO</c> (entrada do historico de escalas do colaborador; <c>alterationDate</c> = vigencia).</summary>
    public static JsonObject WorkScheduleDto(WorkScheduleRecord w, long alterationDate)
    {
        var node = (JsonObject)w.ToNode();
        foreach (var notInDto in new[] { "inactive", "ignoreHoliday", "preAssignedInterval", "showIntradayInTimeSheet" })
        {
            node.Remove(notInDto);
        }

        node["alterationDate"] = alterationDate;
        return node;
    }

    /// <summary><c>EmployeeReturnWithoutPinDTO</c> (ou <c>EmployeeReturnDTO</c> quando <paramref name="includePin"/>).</summary>
    public static JsonObject Employee(EmployeeRecord e, StoreData data, bool includePin)
    {
        var node = new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["fired"] = e.Fired };
        node.Put("externalId", e.ExternalId);
        node.Put("cpf", e.Cpf);
        node.Put("pis", e.Pis);
        node.Put("doubleBindEmployee", e.DoubleBindEmployee);
        node.Put("recordsPunch", e.Fields.GetBool("recordsPunch") ?? true);
        node.Put("canViewWorkgroup", e.Fields.GetObject("devicePermissionsDTO")?.GetObject("appPermissionsDTO")?.GetBool("canViewWorkgroup") ?? false);
        node.Put("updateDate", e.UpdatedAt);

        foreach (var name in new[] { "ctps", "series", "email", "phone", "gender", "socialName", "state", "admissionDate", "birthDate", "effectiveDate" })
        {
            node.PutNode(name, e.Fields[name]?.DeepClone());
        }

        if (e.Fired)
        {
            node.Put("resignationDate", e.ResignationDate);
            node.Put("motivoDemissao", e.ResignationReason);
        }

        if (e.CompanyId is { } companyId && data.Companies.TryGetValue(companyId, out var company))
        {
            node["company"] = CompanyReturn(company);
        }

        if (data.WorkSchedules.TryGetValue(e.WorkScheduleId, out var schedule))
        {
            node["currentWorkSchedule"] = schedule.ToNode();
        }

        if (e.WorkplaceId is { } workplaceId && data.Workplaces.TryGetValue(workplaceId, out var workplace))
        {
            node["currentWorkplaceDTO"] = Workplace(workplace);
            node["workplaceList"] = new JsonArray(Workplace(workplace));
        }

        if (e.JobRoleId is { } jobRoleId && data.JobRoles.TryGetValue(jobRoleId, out var jobRole))
        {
            node["jobRoleDTO"] = jobRole.ToNode();
        }

        var history = new JsonArray();
        foreach (var entry in e.WorkScheduleHistory)
        {
            if (data.WorkSchedules.TryGetValue(entry.WorkScheduleId, out var historic))
            {
                history.Add(WorkScheduleDto(historic, entry.DateInMillis));
            }
        }

        node["workScheduleList"] = history;
        node["managers"] = new JsonArray();

        if (includePin)
        {
            node["pin"] = e.Pin;
        }

        return node;
    }

    /// <summary>Subconjunto do <c>EmployeeDTO</c> usado dentro dos DTOs de lancamento.</summary>
    public static JsonObject EmployeeSummary(EmployeeRecord e)
    {
        var node = new JsonObject { ["id"] = e.Id, ["name"] = e.Name };
        node.Put("externalId", e.ExternalId);
        node.Put("cpf", e.Cpf);
        node.Put("pis", e.Pis);
        node.PutNode("admissionDate", e.Fields["admissionDate"]?.DeepClone());
        node.PutNode("effectiveDate", e.Fields["effectiveDate"]?.DeepClone());
        return node;
    }

    /// <summary><c>AdjustmentReasonRecordResponseDTO</c>.</summary>
    public static JsonObject AdjustmentResponse(AdjustmentRecord a, StoreData data)
    {
        var node = AdjustmentCommon(a, data);
        node.PutNode("adjustmentReasonDTO", data.AdjustmentReasons.TryGetValue(a.AdjustmentReasonId, out var reason) ? reason.ToNode() : null);
        return node;
    }

    /// <summary><c>AdjustmentReasonRecordDTO</c> (entity do update V2).</summary>
    public static JsonObject AdjustmentRecordDto(AdjustmentRecord a, StoreData data)
    {
        var node = AdjustmentCommon(a, data);
        node.PutNode("adjustmentReasonDTO", data.AdjustmentReasons.TryGetValue(a.AdjustmentReasonId, out var reason) ? reason.ToNode() : null);
        node["firstDayIsPartial"] = a.FirstDayIsPartial;
        node["edited"] = a.Edited;
        return node;
    }

    private static JsonObject AdjustmentCommon(AdjustmentRecord a, StoreData data)
    {
        var node = new JsonObject
        {
            ["id"] = a.Id,
            ["startDate"] = a.StartDate,
            ["endDate"] = a.EndDate,
            ["fullDay"] = a.FullDay,
            ["status"] = a.Status,
            ["lastUpdate"] = a.LastUpdate,
        };
        node.Put("observation", a.Observation);
        node.Put("origem", a.Origem);
        node.PutNode("employeeDTO", data.Employees.TryGetValue(a.EmployeeId, out var employee) ? EmployeeSummary(employee) : null);
        return node;
    }
}
