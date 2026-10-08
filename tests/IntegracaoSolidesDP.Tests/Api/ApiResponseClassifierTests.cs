using System.Net;
using IntegracaoSolidesDP.Worker.Api;

namespace IntegracaoSolidesDP.Tests.Api;

public sealed class ApiResponseClassifierTests
{
    [Fact]
    public void ResponseEntity_with_error_inside_http_200_is_a_rejection()
    {
        const string body = """{"body":{"message":"CPF já cadastrado","error":"duplicate_cpf"},"statusCode":"BAD_REQUEST","statusCodeValue":400}""";

        var (outcome, message, _) = ApiResponseClassifier.Classify(HttpStatusCode.OK, body);

        outcome.Should().Be(ApiOutcome.Rejected);
        message.Should().Be("CPF já cadastrado");
    }

    [Fact]
    public void ResponseEntity_success_exposes_the_inner_body()
    {
        const string body = """{"body":{"id":123,"externalId":"1-00000001"},"statusCode":"CREATED","statusCodeValue":201}""";

        var (outcome, _, payload) = ApiResponseClassifier.Classify(HttpStatusCode.OK, body);

        outcome.Should().Be(ApiOutcome.Success);
        payload!.Value.GetProperty("id").GetInt64().Should().Be(123);
    }

    [Fact]
    public void Adjustment_not_registered_is_a_rejection()
    {
        var (outcome, message, _) = ApiResponseClassifier.Classify(HttpStatusCode.OK, """{"registered":false,"message":"Período fechado"}""");

        outcome.Should().Be(ApiOutcome.Rejected);
        message.Should().Be("Período fechado");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ApiOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, ApiOutcome.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, ApiOutcome.Rejected)]
    [InlineData(HttpStatusCode.Conflict, ApiOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiOutcome.TransportError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ApiOutcome.TransportError)]
    public void Http_status_codes_are_classified(HttpStatusCode status, ApiOutcome expected)
    {
        ApiResponseClassifier.Classify(status, """{"status":0,"message":"x"}""").Outcome.Should().Be(expected);
    }

    [Fact]
    public void Spring_error_body_message_is_extracted()
    {
        const string body = """{"timestamp":1,"status":400,"error":"Bad Request","message":"name é obrigatório","path":"/job-role/register"}""";

        ApiResponseClassifier.Classify(HttpStatusCode.BadRequest, body).Message.Should().Be("name é obrigatório");
    }

    [Fact]
    public void Plain_text_bodies_are_tolerated()
    {
        var (outcome, _, payload) = ApiResponseClassifier.Classify(HttpStatusCode.OK, "Token OK");

        outcome.Should().Be(ApiOutcome.Success);
        payload!.Value.GetString().Should().Be("Token OK");
    }
}
