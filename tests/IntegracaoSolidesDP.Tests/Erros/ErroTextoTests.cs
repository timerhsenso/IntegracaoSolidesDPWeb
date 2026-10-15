using IntegracaoSolidesDP.Worker.Erros;

namespace IntegracaoSolidesDP.Tests.Erros;

/// <summary>O texto do erro vai para o suporte, de fora do cliente: sem CPF inteiro, token nem senha.</summary>
public sealed class ErroTextoTests
{
    [Fact]
    public void Cpf_is_masked_and_secrets_are_removed()
    {
        var texto = ErroTexto.Limpar(
            "CPF 52998224725 e 529.982.247-25; Authorization: Bearer abcdef123456; Server=x;Password=Segredo1;User Id=sa; {\"token\":\"tok-123456789\"}",
            2000);

        texto.Should().Contain("529.***.***-25").And.NotContain("52998224725").And.NotContain("982.247");
        texto.Should().Contain("Bearer ***").And.NotContain("abcdef123456");
        texto.Should().Contain("Password=***").And.NotContain("Segredo1");
        texto.Should().NotContain("tok-123456789");
    }

    [Fact]
    public void Long_numbers_that_are_not_a_cpf_stay_and_the_text_is_cut()
    {
        ErroTexto.Limpar("pedido 123456789012345", 2000).Should().Be("pedido 123456789012345");
        ErroTexto.Limpar(new string('x', 50), 10).Should().HaveLength(10);
        ErroTexto.Limpar(null, 10).Should().BeNull();
    }

    [Fact]
    public void The_same_error_with_other_numbers_and_ids_has_the_same_signature()
    {
        var a = ErroTexto.Assinatura("servico", "Pipeline", "System.Exception", $"Execução {Guid.NewGuid()} falhou na linha 10");
        var b = ErroTexto.Assinatura("servico", "Pipeline", "System.Exception", $"Execução {Guid.NewGuid()} falhou na linha 254");
        var outroTipo = ErroTexto.Assinatura("servico", "Pipeline", "System.IO.IOException", "Execução falhou");
        var outraOrigem = ErroTexto.Assinatura("web", "Pipeline", "System.Exception", "Execução falhou");

        a.Should().Be(b).And.HaveLength(64);
        a.Should().NotBe(outroTipo).And.NotBe(outraOrigem);
    }

    [Fact]
    public void The_code_shown_to_the_user_is_e_and_the_id()
    {
        CodigoErro.De(1027).Should().Be("E-1027");
        new ErroAplicacao { Id = 5 }.Codigo.Should().Be("E-5");
    }
}
