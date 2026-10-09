using IntegracaoSolidesDP.Worker.Mapping;

namespace IntegracaoSolidesDP.Tests.Mapping;

public sealed class CodigoExternoTests
{
    [Theory]
    [InlineData(null, "00007811", "vazio vira a matrícula")]
    [InlineData("", "00007811", "vazio vira a matrícula")]
    [InlineData("   ", "00007811", "vazio vira a matrícula")]
    [InlineData("0", "00007811", "zerado vira a matrícula")]
    [InlineData("00000000", "00007811", "zerado vira a matrícula")]
    [InlineData("00007811", "00007811", "igual: nada muda")]
    [InlineData("00000024", "00007811", "8 dígitos diferentes: corrige para a matrícula")]
    [InlineData("CONTAB-55", "CONTAB-55", "código de outro sistema: não mexe")]
    [InlineData("7811", "7811", "não tem 8 dígitos: não mexe")]
    [InlineData("000078110", "000078110", "9 dígitos: não mexe")]
    public void Rule_of_the_external_code(string? atual, string esperado, string motivo)
    {
        CodigoExterno.ParaEnviar(atual, "00007811").Should().Be(esperado, motivo);
    }

    [Fact]
    public void Change_is_described_only_when_the_code_changes()
    {
        CodigoExterno.DescreverMudanca("00007811", "00007811").Should().BeNull();
        CodigoExterno.DescreverMudanca(null, "00007811").Should().Be("Código Externo vazio → 00007811");
        CodigoExterno.DescreverMudanca("00000024", "00007811").Should().Be("Código Externo 00000024 → 00007811");
    }
}
