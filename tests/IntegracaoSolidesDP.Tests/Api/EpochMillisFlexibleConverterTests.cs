using System.Text.Json;
using IntegracaoSolidesDP.Worker.Api;

namespace IntegracaoSolidesDP.Tests.Api;

public sealed class EpochMillisFlexibleConverterTests
{
    private static readonly long Agosto = new DateTimeOffset(2024, 8, 1, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("""{"effectiveDate":1722481200000}""")]
    [InlineData("""{"effectiveDate":"1722481200000"}""")]
    [InlineData("""{"effectiveDate":"2024-08-01T03:00:00.000+0000"}""")]
    [InlineData("""{"effectiveDate":"2024-08-01T00:00:00.000-0300"}""")]
    [InlineData("""{"effectiveDate":"2024-08-01T03:00:00Z"}""")]
    public void Reads_every_date_format_the_api_returns(string json)
    {
        JsonSerializer.Deserialize<EmployeeDto>(json, SolidesDpJson.Options)!.EffectiveDate.Should().Be(Agosto);
    }

    [Theory]
    [InlineData("""{"effectiveDate":null}""")]
    [InlineData("""{"effectiveDate":"não é data"}""")]
    [InlineData("""{}""")]
    public void Unreadable_or_missing_date_is_null(string json)
    {
        JsonSerializer.Deserialize<EmployeeDto>(json, SolidesDpJson.Options)!.EffectiveDate.Should().BeNull();
    }

    [Fact]
    public void Current_job_role_comes_from_job_role_dto()
    {
        var dto = JsonSerializer.Deserialize<EmployeeDto>("""{"jobRoleDTO":{"id":9,"externalId":"005","description":"AUX"}}""", SolidesDpJson.Options)!;

        dto.JobRole!.ExternalId.Should().Be("005");
    }

    [Theory]
    [InlineData("""{"company":{"id":3001,"fantasyName":"ADN"},"phone":"71999990000","currentWorkplaceDTO":{"externalId":"15-1"}}""")]
    [InlineData("""{"company":3001,"phone":71999990000,"currentWorkplaceDTO":"15-1"}""")]
    public void Conference_fields_accept_object_or_id_and_text_or_number(string json)
    {
        var dto = JsonSerializer.Deserialize<EmployeeDto>(json, SolidesDpJson.Options)!;

        dto.Company!.Id.Should().Be(3001);
        dto.Phone.Should().Be("71999990000");
        dto.CurrentWorkplace?.ExternalId.Should().BeOneOf("15-1", null);
    }
}
