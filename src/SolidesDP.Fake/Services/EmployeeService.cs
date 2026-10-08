using System.Globalization;
using System.Text.Json.Nodes;
using SolidesDP.Fake.Configuration;
using SolidesDP.Fake.Domain;
using SolidesDP.Fake.Http;
using SolidesDP.Fake.Store;

namespace SolidesDP.Fake.Services;

/// <summary>Resultado de um register de colaborador.</summary>
internal sealed record RegisterOutcome(EmployeeRecord Employee, bool Created);

/// <summary>Regras de negocio de colaborador (registro/atualizacao, busca e demissao). Chamar sempre dentro do lock da store.</summary>
internal static class EmployeeService
{
    /// <summary>Propriedades do <c>EmployeeDTO</c> com tratamento proprio (nao vao para <see cref="EmployeeRecord.Fields"/>).</summary>
    private static readonly HashSet<string> HandledProperties = new(StringComparer.Ordinal)
    {
        "id", "tangerinoId", "message", "name", "externalId", "cpf", "pis", "doubleBindEmployee",
        "jobRole", "jobRoleExternalId", "workplace", "workplaceExternalId", "company", "companyExternalId",
        "workSchedule", "workScheduleExternalId", "punchRuleExternalId", "workScheduleDateInMillis", "punchRuleDateInMillis",
    };

    /// <summary>Procura por externalId: o nao demitido tem prioridade; senao o demitido mais recente (salvo <paramref name="ignoreFired"/>).</summary>
    public static EmployeeRecord? FindByExternalId(StoreData data, string externalId, bool ignoreFired = false)
    {
        var matches = data.Employees.Values.Where(e => e.ExternalId == externalId).ToList();
        return matches.FirstOrDefault(e => !e.Fired) ?? (ignoreFired ? null : matches.LastOrDefault());
    }

    /// <summary>Procura por <c>tangerinoId</c> (prioritario) ou <c>externalId</c>.</summary>
    public static EmployeeRecord? Find(StoreData data, long? tangerinoId, string? externalId, bool ignoreFired)
    {
        EmployeeRecord? found = null;
        if (tangerinoId is { } id)
        {
            data.Employees.TryGetValue(id, out found);
            if (found is { Fired: true } && ignoreFired)
            {
                found = null;
            }
        }
        else if (externalId is not null)
        {
            found = FindByExternalId(data, externalId, ignoreFired);
        }

        return found;
    }

    public static RegisterOutcome Register(StoreData data, JsonObject body, bool allowUpdate, long? managerId, FakeBehavior behavior, long now)
    {
        var incoming = Normalize(body);
        var cpf = incoming.GetString("cpf");
        var pis = incoming.GetString("pis");
        if (cpf is not null && !IsEleven(cpf))
        {
            throw new ApiException(ApiError.BadRequest(ErrorCodes.InvalidValue, $"Invalid 'cpf': must have exactly 11 digits (got '{cpf}')"));
        }

        if (pis is not null && !IsEleven(pis))
        {
            throw new ApiException(ApiError.BadRequest(ErrorCodes.InvalidValue, $"Invalid 'pis': must have exactly 11 digits (got '{pis}')"));
        }

        var target = SelectTarget(data, incoming, allowUpdate, behavior);
        var keep = target is not null && behavior.UpdateOmittedFields == UpdateOmittedFieldsMode.Keep;

        var jobRole = Resolve(incoming, "jobRole", "jobRoleExternalId", "Job role", id => data.JobRoles.GetValueOrDefault(id), ext => data.JobRoles.Values.FirstOrDefault(j => j.ExternalId == ext));
        var workplace = Resolve(incoming, "workplace", "workplaceExternalId", "Workplace", id => data.Workplaces.GetValueOrDefault(id), ext => data.Workplaces.Values.FirstOrDefault(w => w.ExternalId == ext));
        var company = Resolve(incoming, "company", "companyExternalId", "Company", id => data.Companies.GetValueOrDefault(id), ext => data.Companies.Values.FirstOrDefault(c => c.ExternalId == ext));
        var schedule = Resolve(incoming, "workSchedule", "workScheduleExternalId", "Work schedule", id => data.WorkSchedules.GetValueOrDefault(id), ext => data.WorkSchedules.Values.FirstOrDefault(s => s.ExternalId == ext));
        var punchRule = incoming.GetString("punchRuleExternalId") is { } punchExternalId
            ? new Resolved<PunchRuleRecord>(true, data.PunchRules.Values.FirstOrDefault(p => p.ExternalId == punchExternalId)
                ?? throw InvalidReference($"Punch rule with externalId '{punchExternalId}' does not exist"))
            : new Resolved<PunchRuleRecord>(false, null);

        var scheduleId = schedule.Provided
            ? schedule.Entity!.Id
            : keep ? target!.WorkScheduleId : StandardSchedule(data).Id;
        var punchRuleId = punchRule.Provided
            ? punchRule.Entity!.Id
            : keep ? target!.PunchRuleId : StandardPunchRule(data).Id;

        var externalId = incoming.GetString("externalId") ?? (keep ? target!.ExternalId : null);
        if (target is not null && externalId is not null && data.Employees.Values.Any(e => e.Id != target.Id && !e.Fired && e.ExternalId == externalId))
        {
            throw new ApiException(ApiError.Conflict(ErrorCodes.AlreadyExists, $"Another active employee already uses externalId '{externalId}'"));
        }

        cpf ??= keep ? target!.Cpf : null;
        pis ??= keep ? target!.Pis : null;
        var requestDoubleBind = incoming.GetBool("doubleBindEmployee");
        if (behavior.UniqueCpfAmongActive && cpf is not null && requestDoubleBind != true)
        {
            var clash = data.Employees.Values.FirstOrDefault(e => (target is null || e.Id != target.Id) && !e.Fired && e.Cpf == cpf);
            if (clash is not null)
            {
                throw new ApiException(ApiError.Conflict(
                    ErrorCodes.DuplicateCpf,
                    $"CPF {cpf} already belongs to employee {clash.Id}; send doubleBindEmployee=true to allow a double bond"));
            }
        }

        var fields = new JsonObject();
        if (keep)
        {
            foreach (var (name, value) in target!.Fields)
            {
                fields[name] = value?.DeepClone();
            }
        }

        foreach (var (name, value) in incoming)
        {
            if (!HandledProperties.Contains(name))
            {
                fields[name] = value!.DeepClone();
            }
        }

        var scheduleDate = incoming.GetLong("workScheduleDateInMillis");
        var history = (target?.WorkScheduleHistory ?? []).ToList();
        var last = history.LastOrDefault();
        if (last is null || last.WorkScheduleId != scheduleId || last.DateInMillis != scheduleDate)
        {
            history.Add(new WorkScheduleHistoryEntry { WorkScheduleId = scheduleId, DateInMillis = scheduleDate ?? 0, RegisteredAt = now });
        }

        var id = target?.Id ?? data.NextId(EntityKind.Employee);
        var record = new EmployeeRecord
        {
            Id = id,
            ExternalId = externalId,
            Name = incoming.GetString("name") ?? target?.Name ?? string.Empty,
            Cpf = cpf,
            Pis = pis,
            DoubleBindEmployee = requestDoubleBind ?? (keep && target!.DoubleBindEmployee),
            Fired = false, // criacao, atualizacao de ativo e reativacao resultam, todas, em colaborador ativo
            CompanyId = company.Provided ? company.Entity!.Id : keep ? target!.CompanyId : null,
            WorkplaceId = workplace.Provided ? workplace.Entity!.Id : keep ? target!.WorkplaceId : null,
            JobRoleId = jobRole.Provided ? jobRole.Entity!.Id : keep ? target!.JobRoleId : null,
            WorkScheduleId = scheduleId,
            WorkScheduleDateInMillis = scheduleDate ?? (keep ? target!.WorkScheduleDateInMillis : null),
            PunchRuleId = punchRuleId,
            PunchRuleDateInMillis = incoming.GetLong("punchRuleDateInMillis") ?? (keep ? target!.PunchRuleDateInMillis : null),
            WorkScheduleHistory = history,
            LinkedManagerId = managerId ?? target?.LinkedManagerId,
            Pin = target?.Pin ?? GeneratePin(id),
            CreatedAt = target?.CreatedAt ?? now,
            UpdatedAt = now,
            Fields = fields,
        };

        data.Employees[id] = record;
        return new RegisterOutcome(record, target is null);
    }

    public static EmployeeRecord Dismiss(StoreData data, JsonObject body, long now)
    {
        var tangerinoId = body.GetLong("tangerinoId");
        var externalId = body.GetString("externalId");
        if (tangerinoId is null && string.IsNullOrWhiteSpace(externalId))
        {
            throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'externalId' or 'tangerinoId' is required"));
        }

        var resignationDate = body.GetLong("resignationDate")
            ?? throw new ApiException(ApiError.BadRequest(ErrorCodes.RequiredField, "'resignationDate' is required"));

        var employee = Find(data, tangerinoId, string.IsNullOrWhiteSpace(externalId) ? null : externalId, ignoreFired: false)
            ?? throw new ApiException(ApiError.NotFound("Employee not found"));
        if (employee.Fired)
        {
            throw new ApiException(ApiError.Conflict(ErrorCodes.AlreadyFired, $"Employee {employee.Id} is already dismissed"));
        }

        var dismissed = employee with
        {
            Fired = true,
            ResignationDate = resignationDate,
            ResignationReason = body.GetString("resignationReason"),
            UpdatedAt = now,
        };
        data.Employees[dismissed.Id] = dismissed;
        return dismissed;
    }

    /// <summary>Decide qual registro o register vai atualizar (nulo = criar um novo); lanca o erro adequado quando nao pode prosseguir.</summary>
    private static EmployeeRecord? SelectTarget(StoreData data, JsonObject incoming, bool allowUpdate, FakeBehavior behavior)
    {
        EmployeeRecord? existing;
        if (incoming.GetLong("tangerinoId") is { } tangerinoId)
        {
            existing = data.Employees.GetValueOrDefault(tangerinoId)
                ?? throw new ApiException(ApiError.NotFound($"Employee with tangerinoId {tangerinoId} not found"));
        }
        else if (incoming.GetString("externalId") is { } externalId)
        {
            existing = FindByExternalId(data, externalId);
        }
        else
        {
            return null;
        }

        if (existing is null)
        {
            return null;
        }

        if (!existing.Fired)
        {
            return allowUpdate
                ? existing
                : throw new ApiException(ApiError.Conflict(ErrorCodes.AlreadyExists, $"Employee {existing.Id} already exists (externalId '{existing.ExternalId}'); use allowUpdate=true to update it"));
        }

        return behavior.RegisterFiredExternalId switch
        {
            RegisterFiredExternalIdMode.CreateNew => null,
            RegisterFiredExternalIdMode.Reactivate => existing,
            _ => throw new ApiException(ApiError.Conflict(ErrorCodes.EmployeeFired, $"Employee {existing.Id} (externalId '{existing.ExternalId}') is dismissed and cannot be registered/updated")),
        };
    }

    private static JsonObject Normalize(JsonObject body)
    {
        var result = new JsonObject();
        foreach (var (name, value) in body)
        {
            if (value is null || (value is JsonValue v && v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text)))
            {
                continue;
            }

            result[name] = value.DeepClone();
        }

        return result;
    }

    private static Resolved<T> Resolve<T>(JsonObject body, string idProperty, string externalIdProperty, string label, Func<long, T?> byId, Func<string, T?> byExternalId)
        where T : class
    {
        if (body.GetLong(idProperty) is { } id)
        {
            return new Resolved<T>(true, byId(id) ?? throw InvalidReference($"{label} with id {id} does not exist"));
        }

        if (body.GetString(externalIdProperty) is { } externalId)
        {
            return new Resolved<T>(true, byExternalId(externalId) ?? throw InvalidReference($"{label} with externalId '{externalId}' does not exist"));
        }

        return new Resolved<T>(false, null);
    }

    private static WorkScheduleRecord StandardSchedule(StoreData data) =>
        data.WorkSchedules.Values.FirstOrDefault(s => s.Standard) ?? throw InvalidReference("There is no standard work schedule configured");

    private static PunchRuleRecord StandardPunchRule(StoreData data) =>
        data.PunchRules.Values.FirstOrDefault(p => p.Standard) ?? throw InvalidReference("There is no standard punch rule configured");

    private static ApiException InvalidReference(string message) => new(ApiError.BadRequest(ErrorCodes.InvalidReference, message));

    private static bool IsEleven(string value) => value.Length == 11 && value.All(char.IsAsciiDigit);

    private static string GeneratePin(long id) => (100000 + (id * 7919 % 900000)).ToString(CultureInfo.InvariantCulture);

    private sealed record Resolved<T>(bool Provided, T? Entity)
        where T : class;
}
