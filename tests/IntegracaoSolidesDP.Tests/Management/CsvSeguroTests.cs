using IntegracaoSolidesDP.Worker.Management;

namespace IntegracaoSolidesDP.Tests.Management;

/// <summary>CSV aberto no Excel: texto que começa como fórmula vira texto.</summary>
public sealed class CsvSeguroTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("\tx", "'\tx")]
    public void Text_that_starts_like_a_formula_becomes_text(string valor, string esperado) =>
        CsvSeguro.Neutralizar(valor).Should().Be(esperado);

    [Theory]
    [InlineData("-")]
    [InlineData("-12")]
    [InlineData("- sem cargo")]
    [InlineData("MARIA DA SILVA")]
    [InlineData("15-00007811")]
    [InlineData("")]
    public void Ordinary_values_stay_as_they_are(string valor) => CsvSeguro.Neutralizar(valor).Should().Be(valor);
}
