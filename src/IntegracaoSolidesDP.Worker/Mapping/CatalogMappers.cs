using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Mapping;

public static class JobRoleMapper
{
    public static JobRoleRequest? Map(JobRoleRow row)
    {
        var description = EmployeeMapper.Clean(row.Descricao);
        var code = EmployeeMapper.Clean(row.Cdcargo);
        return description is null || code is null ? null : new JobRoleRequest(description, code, EmployeeMapper.Clean(row.Cbo));
    }
}

public static class WorkplaceMapper
{
    /// <summary>
    /// O código vai no nome porque o nome fantasia se repete entre filiais
    /// (ex.: "ATMOS" em 8-1 e 8-2, "AMAZON TECHNOLOGIES" em 4-1 e 5-1).
    /// </summary>
    public static WorkplaceRequest Map(WorkplaceRow row)
    {
        var name = EmployeeMapper.Clean(row.NomeFantasia) ?? EmployeeMapper.Clean(row.Descricao) ?? "FILIAL";
        return new WorkplaceRequest($"{name} ({row.ExternalId})", row.ExternalId);
    }
}
