using System.Text;

namespace SolidesDP.Fake.Tests;

public sealed class EmployeeValidationTests : FakeTestBase
{
    private static readonly string[] RequiredFields = ["admissionDate", "effectiveDate", "name", "punchRuleDateInMillis", "workScheduleDateInMillis"];

    [Theory]
    [InlineData("admissionDate")]
    [InlineData("effectiveDate")]
    [InlineData("name")]
    [InlineData("punchRuleDateInMillis")]
    [InlineData("workScheduleDateInMillis")]
    public async Task Each_required_field_is_enforced(string missing)
    {
        var payload = Payloads.Employee("E-1");
        payload.Remove(missing);

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "required_field", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Contain(missing);
        (await StateAsync()).Employees.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_name_counts_as_missing_and_all_missing_fields_are_listed()
    {
        var response = await RegisterEmployeeAsync(new JsonObject { ["name"] = "  " });

        AssertEnvelopeError(response, "required_field", 400);
        var message = response["body"]!["message"]!.GetValue<string>();
        RequiredFields.Should().OnlyContain(f => message.Contains(f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_top_level_field_is_rejected()
    {
        var payload = Payloads.Employee("E-1");
        payload["campoInventado"] = 1;

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "unknown_field", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Contain("campoInventado");
    }

    [Fact]
    public async Task Unknown_nested_field_is_rejected_with_its_path()
    {
        var payload = Payloads.Employee("E-1");
        payload["employeeAddress"] = new JsonObject { ["street"] = "RUA A", ["rua"] = "RUA A" };

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "unknown_field", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Contain("employeeAddress.rua");
    }

    [Fact]
    public async Task Field_names_are_case_sensitive_like_jackson()
    {
        var payload = Payloads.Employee("E-1");
        payload["Cpf"] = "12345678901";

        AssertEnvelopeError(await RegisterEmployeeAsync(payload), "unknown_field", 400);
    }

    [Fact]
    public async Task Unknown_fields_are_accepted_when_RejectUnknownFields_is_off()
    {
        await SetBehaviorAsync(b => b.RejectUnknownFields = false);
        var payload = Payloads.Employee("E-1");
        payload["campoInventado"] = 1;

        var response = await RegisterEmployeeAsync(payload);

        response["statusCodeValue"]!.GetValue<int>().Should().Be(201);
    }

    [Fact]
    public async Task Unknown_fields_are_reported_before_missing_required_fields()
    {
        var response = await RegisterEmployeeAsync(new JsonObject { ["foo"] = 1 });

        AssertEnvelopeError(response, "unknown_field", 400);
    }

    [Theory]
    [InlineData("gender", "X")]
    [InlineData("genderIdentity", "OUTRO")]
    [InlineData("maritalStatus", "COMPLICADO")]
    [InlineData("educationLevel", "DOUTOR")]
    [InlineData("raceColor", "VERDE")]
    [InlineData("typeOfLaborRelationship", "PJ")]
    [InlineData("admissionType", "CONTRATACAO")]
    [InlineData("timezone", "MARTE")]
    [InlineData("hiringType", "TEMPORARIO")]
    [InlineData("workRegimeType", "HOME")]
    public async Task Invalid_enum_values_are_rejected(string field, string value)
    {
        var payload = Payloads.Employee("E-1");
        payload[field] = value;

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "invalid_value", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Contain(field);
    }

    [Theory]
    [InlineData("gender", "MASCULINO")]
    [InlineData("maritalStatus", "UNIAO_ESTAVEL")]
    [InlineData("educationLevel", "SUPERIOR_COMPLETO")]
    [InlineData("raceColor", "NAO_INFORMADO")]
    [InlineData("typeOfLaborRelationship", "JOVEM_APRENDIZ")]
    [InlineData("admissionType", "ADMISSAO")]
    [InlineData("timezone", "SAO_PAULO")]
    [InlineData("hiringType", "CONTRATACAO_NORMAL")]
    public async Task Valid_enum_values_are_accepted(string field, string value)
    {
        var payload = Payloads.Employee("E-1");
        payload[field] = value;

        var response = await RegisterEmployeeAsync(payload);

        response["statusCodeValue"]!.GetValue<int>().Should().Be(201);
    }

    [Fact]
    public async Task Wrong_json_types_are_rejected()
    {
        var payload = Payloads.Employee("E-1");
        payload["admissionDate"] = "2024-01-01";
        payload["recordsPunch"] = "sim";

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "invalid_value", 400);
        var message = response["body"]!["message"]!.GetValue<string>();
        message.Should().Contain("admissionDate").And.Contain("recordsPunch");
    }

    [Fact]
    public async Task Date_time_properties_accept_epoch_millis_or_iso_strings()
    {
        var payload = Payloads.Employee("E-1");
        payload["dataContrato"] = "2024-01-01T00:00:00.000Z"; // Date no EmployeeDTO (string date-time)

        (await RegisterEmployeeAsync(payload))["statusCodeValue"]!.GetValue<int>().Should().Be(201);

        payload["dataContrato"] = "ontem";
        AssertEnvelopeError(await RegisterEmployeeAsync(payload, allowUpdate: true), "invalid_value", 400);
    }

    [Theory]
    [InlineData("cpf", "123")]
    [InlineData("cpf", "123.456.789-01")]
    [InlineData("cpf", "123456789012")]
    [InlineData("cpf", "1234567890a")]
    [InlineData("pis", "1234567890")]
    [InlineData("pis", "123.45678.90-1")]
    public async Task Cpf_and_pis_must_have_exactly_eleven_digits(string field, string value)
    {
        var payload = Payloads.Employee("E-1");
        payload[field] = value;

        var response = await RegisterEmployeeAsync(payload);

        AssertEnvelopeError(response, "invalid_value", 400);
        response["body"]!["message"]!.GetValue<string>().Should().Contain(field);
    }

    [Fact]
    public async Task Blank_cpf_is_treated_as_absent()
    {
        var payload = Payloads.Employee("E-1");
        payload["cpf"] = string.Empty;

        (await RegisterEmployeeAsync(payload))["statusCodeValue"]!.GetValue<int>().Should().Be(201);
        (await StateAsync()).Employees.Single().Cpf.Should().BeNull();
    }

    [Fact]
    public async Task Non_json_content_type_is_a_415_regardless_of_the_error_style()
    {
        using var content = new StringContent(Payloads.Employee("E-1").ToJsonString(), Encoding.UTF8, "text/plain");

        using var response = await Host.Api.PostAsync(Rel("/employee/register"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        AssertSpringError(await ReadAsync(response), 415, "/employee/register");
    }

    [Fact]
    public async Task Json_with_charset_content_type_is_accepted()
    {
        using var content = new StringContent(Payloads.Employee("E-1").ToJsonString(), Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "utf-8";

        using var response = await Host.Api.PostAsync(Rel("/employee/register"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Empty_body_is_a_spring_400()
    {
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

        using var response = await Host.Api.PostAsync(Rel("/employee/register"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400, "/employee/register");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"texto\"")]
    public async Task Malformed_or_non_object_json_is_a_spring_400(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Host.Api.PostAsync(Rel("/employee/register"), content, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400);
    }

    [Fact]
    public async Task Invalid_query_boolean_is_a_spring_400()
    {
        using var response = await PostAsync("/employee/register?allowUpdate=talvez", Payloads.Employee("E-1"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertSpringError(await ReadAsync(response), 400);
    }
}
