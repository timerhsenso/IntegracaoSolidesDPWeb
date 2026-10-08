using System.Text.Json.Nodes;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Services;

/// <summary>Dados de um lancamento novo, ja extraidos do corpo (id ou externalId para o colaborador).</summary>
internal sealed record AdjustmentInput
{
    public long? EmployeeId { get; init; }

    public string? EmployeeExternalId { get; init; }

    public long? ReasonId { get; init; }

    public long? StartDate { get; init; }

    public long? EndDate { get; init; }

    public bool? FullDay { get; init; }

    public string? Status { get; init; }

    public string? Observation { get; init; }

    public string? Origem { get; init; }

    public bool? FirstDayIsPartial { get; init; }
}

/// <summary>Alteracao parcial de um lancamento (so o que veio preenchido muda).</summary>
internal sealed record AdjustmentPatch
{
    public long? EmployeeId { get; init; }

    public long? ReasonId { get; init; }

    public long? StartDate { get; init; }

    public long? EndDate { get; init; }

    public bool? FullDay { get; init; }

    public string? Status { get; init; }

    public string? Observation { get; init; }

    public string? Origem { get; init; }

    public bool? FirstDayIsPartial { get; init; }

    public bool? Excluded { get; init; }
}

/// <summary>Regras de negocio de lancamentos (ferias, abonos, atestados...). Chamar sempre dentro do lock da store.</summary>
internal static class AdjustmentService
{
    public static AdjustmentRecord Register(StoreData data, AdjustmentInput input, FakeBehavior behavior, long now)
    {
        if (input.EmployeeId is null && string.IsNullOrWhiteSpace(input.EmployeeExternalId))
        {
            throw Required("the employee (id or externalId) is required");
        }

        var reasonId = input.ReasonId ?? throw Required("the adjustment reason id is required");
        var startDate = input.StartDate ?? throw Required("'startDate' is required");
        var endDate = input.EndDate ?? throw Required("'endDate' is required");

        var employee = input.EmployeeId is { } id
            ? data.Employees.GetValueOrDefault(id) ?? throw InvalidReference($"Employee with id {id} does not exist")
            : EmployeeService.FindByExternalId(data, input.EmployeeExternalId!)
              ?? throw InvalidReference($"Employee with externalId '{input.EmployeeExternalId}' does not exist");
        if (employee.Fired)
        {
            throw new ApiException(ApiError.Conflict(ErrorCodes.EmployeeFired, $"Employee {employee.Id} is dismissed"));
        }

        var reason = FindReason(data, reasonId);
        EnsureRange(startDate, endDate);
        EnsureNoOverlap(data, employee.Id, startDate, endDate, ignoreId: null, behavior);

        var adjustment = new AdjustmentRecord
        {
            Id = data.NextId(EntityKind.Adjustment),
            EmployeeId = employee.Id,
            AdjustmentReasonId = reason.Id,
            StartDate = startDate,
            EndDate = endDate,
            FullDay = input.FullDay ?? reason.FullDay,
            Status = input.Status ?? "APROVADO",
            Observation = input.Observation,
            Origem = input.Origem,
            FirstDayIsPartial = input.FirstDayIsPartial ?? false,
            CreatedAt = now,
            LastUpdate = now,
        };
        data.Adjustments[adjustment.Id] = adjustment;
        return adjustment;
    }

    public static AdjustmentRecord Update(StoreData data, long id, AdjustmentPatch patch, FakeBehavior behavior, long now)
    {
        var existing = data.Adjustments.GetValueOrDefault(id) ?? throw new ApiException(ApiError.NotFound($"Adjustment {id} not found"));

        var employeeId = existing.EmployeeId;
        if (patch.EmployeeId is { } newEmployeeId && newEmployeeId != existing.EmployeeId)
        {
            var employee = data.Employees.GetValueOrDefault(newEmployeeId) ?? throw InvalidReference($"Employee with id {newEmployeeId} does not exist");
            if (employee.Fired)
            {
                throw new ApiException(ApiError.Conflict(ErrorCodes.EmployeeFired, $"Employee {employee.Id} is dismissed"));
            }

            employeeId = newEmployeeId;
        }

        var reasonId = patch.ReasonId is { } newReasonId ? FindReason(data, newReasonId).Id : existing.AdjustmentReasonId;
        var startDate = patch.StartDate ?? existing.StartDate;
        var endDate = patch.EndDate ?? existing.EndDate;
        EnsureRange(startDate, endDate);

        var excluded = patch.Excluded ?? existing.Excluded;
        if (!excluded)
        {
            EnsureNoOverlap(data, employeeId, startDate, endDate, ignoreId: id, behavior);
        }

        var updated = existing with
        {
            EmployeeId = employeeId,
            AdjustmentReasonId = reasonId,
            StartDate = startDate,
            EndDate = endDate,
            FullDay = patch.FullDay ?? existing.FullDay,
            Status = patch.Status ?? existing.Status,
            Observation = patch.Observation ?? existing.Observation,
            Origem = patch.Origem ?? existing.Origem,
            FirstDayIsPartial = patch.FirstDayIsPartial ?? existing.FirstDayIsPartial,
            Excluded = excluded,
            Edited = true,
            LastUpdate = now,
        };
        data.Adjustments[id] = updated;
        return updated;
    }

    public static AdjustmentInput ReadInput(JsonObject body, bool nestedDtos)
    {
        var employee = nestedDtos ? body.GetObject("employeeDTO") : body;
        var reason = nestedDtos ? body.GetObject("adjustmentReasonDTO") : body;
        return new AdjustmentInput
        {
            EmployeeId = nestedDtos ? employee?.GetLong("id") : body.GetLong("employeeId"),
            EmployeeExternalId = nestedDtos ? employee?.GetString("externalId") : body.GetString("employeeExternalId"),
            ReasonId = nestedDtos ? reason?.GetLong("id") : body.GetLong("adjustmentReasonId"),
            StartDate = body.GetLong("startDate"),
            EndDate = body.GetLong("endDate"),
            FullDay = body.GetBool("fullDay"),
            Status = body.GetString("status"),
            Observation = body.GetString("observation"),
            Origem = body.GetString("origem"),
            FirstDayIsPartial = body.GetBool("firstDayIsPartial"),
        };
    }

    private static AdjustmentReasonRecord FindReason(StoreData data, long reasonId)
    {
        var reason = data.AdjustmentReasons.GetValueOrDefault(reasonId) ?? throw InvalidReference($"Adjustment reason with id {reasonId} does not exist");
        return reason.Active ? reason : throw InvalidReference($"Adjustment reason {reasonId} is inactive");
    }

    private static void EnsureRange(long startDate, long endDate)
    {
        if (startDate > endDate)
        {
            throw new ApiException(ApiError.BadRequest(ErrorCodes.InvalidDateRange, "'startDate' must be less than or equal to 'endDate'"));
        }
    }

    private static void EnsureNoOverlap(StoreData data, long employeeId, long startDate, long endDate, long? ignoreId, FakeBehavior behavior)
    {
        if (!behavior.RejectOverlappingAdjustments)
        {
            return;
        }

        var clash = data.Adjustments.Values.FirstOrDefault(a =>
            a.EmployeeId == employeeId && !a.Excluded && a.Id != ignoreId && a.StartDate <= endDate && startDate <= a.EndDate);
        if (clash is not null)
        {
            throw new ApiException(ApiError.Conflict(
                ErrorCodes.Overlap,
                $"The period overlaps adjustment {clash.Id} of the same employee ({clash.StartDate} - {clash.EndDate})"));
        }
    }

    private static ApiException Required(string what) => new(ApiError.BadRequest(ErrorCodes.RequiredField, $"Required: {what}"));

    private static ApiException InvalidReference(string message) => new(ApiError.BadRequest(ErrorCodes.InvalidReference, message));
}
