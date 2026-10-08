using IntegracaoSolidesDP.Worker.Mapping;

namespace IntegracaoSolidesDP.Tests.Mapping;

public sealed class DocumentsTests
{
    [Theory]
    [InlineData("52998224725", "52998224725", null)]
    [InlineData("529.982.247-25", "52998224725", null)]
    [InlineData("1234567890", "01234567890", "cpf_zero_padded")]
    public void Valid_cpfs_are_normalized_to_eleven_digits(string raw, string expected, string? warning)
    {
        Documents.NormalizeCpf(raw).Should().Be((expected, warning));
    }

    [Theory]
    [InlineData(null, "missing_cpf")]
    [InlineData("   ", "missing_cpf")]
    [InlineData("52998224726", "invalid_cpf_check_digit")]
    [InlineData("11111111111", "invalid_cpf_check_digit")]
    [InlineData("1234", "invalid_cpf_length:4")]
    public void Invalid_cpfs_are_dropped_with_a_reason(string? raw, string warning)
    {
        Documents.NormalizeCpf(raw).Should().Be((null, warning));
    }

    [Theory]
    [InlineData("12345678919", true)]
    [InlineData("12345678910", false)]
    public void Pis_check_digit_is_validated(string pis, bool valid)
    {
        Documents.IsValidPis(pis).Should().Be(valid);
    }
}
