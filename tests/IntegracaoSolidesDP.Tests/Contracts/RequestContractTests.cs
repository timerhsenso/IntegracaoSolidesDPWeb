using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;
using IntegracaoSolidesDP.Worker.Mapping;

namespace IntegracaoSolidesDP.Tests.Contracts;

/// <summary>
/// O worker não pode enviar nada que o Swagger do Sólides DP não declare: campo inexistente,
/// valor de enum desconhecido ou falta de obrigatório. Não há homologação para descobrir isso depois.
/// </summary>
public sealed class RequestContractTests
{
    private static readonly SwaggerSpec Spec = SwaggerSpec.Current;

    public static TheoryData<string, string, Type> Bodies => new()
    {
        { "/employee/register", "post", typeof(EmployeeRequest) },
        { "/employee/dismiss", "post", typeof(DismissRequest) },
        { "/job-role/register", "post", typeof(JobRoleRequest) },
        { "/workplace/register", "post", typeof(WorkplaceRequest) },
        { "/companies", "post", typeof(CompanyRequest) },
        { "/adjustment/register/1.1", "post", typeof(AdjustmentRegisterRequest) },
        { "/adjustment/update/{id}", "put", typeof(AdjustmentUpdateRequest) },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Every_property_sent_exists_in_the_swagger_body_definition(string path, string method, Type requestType)
    {
        var definition = Spec.BodyDefinition(path, method);
        var allowed = Spec.Properties(definition);

        var sent = requestType.GetProperties()
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToList();

        sent.Should().NotBeEmpty();
        sent.Except(allowed).Should().BeEmpty($"{requestType.Name} só pode usar campos de {definition}");
    }

    [Fact]
    public void Employee_request_built_by_the_pipeline_carries_every_required_field()
    {
        var request = TestData.FullEmployeeRequest();
        var json = JsonSerializer.SerializeToElement(request, SolidesDpJson.Options);
        var present = json.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Spec.Required("EmployeeDTO").Except(present).Should().BeEmpty();
    }

    [Fact]
    public void Nulls_are_omitted_from_payloads()
    {
        var json = JsonSerializer.Serialize(new EmployeeRequest { Name = "X" }, SolidesDpJson.Options);

        json.Should().Be("""{"name":"X"}""");
    }

    public static TheoryData<string, string, IEnumerable<string>> EnumMaps => new()
    {
        { "EmployeeDTO", "gender", CodeMaps.Gender.Values },
        { "EmployeeDTO", "maritalStatus", CodeMaps.MaritalStatus.Values },
        { "EmployeeDTO", "educationLevel", CodeMaps.EducationLevel.Values },
        { "EmployeeDTO", "raceColor", CodeMaps.RaceColor.Values },
        { "EmployeeDTO", "typeOfLaborRelationship", CodeMaps.LaborRelationship.Values },
        { "DismissDTO", "resignationReason", CodeMaps.DefaultResignationReason.Values.Append(CodeMaps.ResignationReasonFallback).Append(CodeMaps.ResignationReasonTransfer) },
        { "AdjustmentReasonRegisterDTO", "status", ["APROVADO", "PENDENTE", "REPROVADO"] },
        { "AdjustmentReasonRecordUpdateV2DTO", "status", ["APROVADO", "PENDENTE", "REPROVADO"] },
    };

    [Theory]
    [MemberData(nameof(EnumMaps))]
    public void Every_mapped_code_is_a_value_the_swagger_accepts(string definition, string property, IEnumerable<string> values)
    {
        values.Except(Spec.Enum(definition, property)).Should().BeEmpty();
    }

    [Fact]
    public void Resignation_reason_whitelist_matches_the_swagger()
    {
        CodeMaps.ResignationReasons.Should().BeEquivalentTo(Spec.Enum("DismissDTO", "resignationReason"));
    }

    [Theory]
    [InlineData("/employee/register", "post", "allowUpdate")]
    [InlineData("/employee/register", "post", "skipUnifiedSync")]
    [InlineData("/workplace/register", "post", "allowUpdate")]
    [InlineData("/employee/find", "get", "externalId")]
    [InlineData("/employee/find", "get", "tangerinoId")]
    [InlineData("/job-role/find", "get", "externalId")]
    [InlineData("/workplace/find", "get", "externalId")]
    [InlineData("/adjustment/find-all", "get", "employeeId")]
    [InlineData("/adjustment/find-all", "get", "adjustmentReasonId")]
    [InlineData("/adjustment/find-all", "get", "ignoreExcluded")]
    [InlineData("/companies", "get", "pageNumber")]
    [InlineData("/v2/punch-rule", "get", "pageNumber")]
    [InlineData("/work-schedule", "get", "page")]
    [InlineData("/adjustment-reason/find-all", "get", "page")]
    public void Query_parameters_used_by_the_client_exist(string path, string method, string parameter)
    {
        Spec.QueryParameters(path, method).Should().Contain(parameter);
    }

    [Theory]
    [InlineData("/test", "get")]
    [InlineData("/companies", "get")]
    [InlineData("/work-schedule", "get")]
    [InlineData("/v2/punch-rule", "get")]
    [InlineData("/adjustment-reason/find-all", "get")]
    public void Discovery_endpoints_exist(string path, string method)
    {
        Spec.HasOperation(path, method).Should().BeTrue();
    }
}
