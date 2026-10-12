using IntegracaoSolidesDP.Worker.Pipeline;

namespace IntegracaoSolidesDP.Tests.Pipeline;

public sealed class VinculacaoReportTests
{
    [Fact]
    public void Lines_are_ordered_by_what_needs_a_decision_and_then_by_name()
    {
        var csv = VinculacaoReport.ToCsv(
        [
            new VinculacaoLinha { Rotulo = "15-3", Nome = "ZÉLIA", Resultado = ResultadosVinculacao.SemAlteracao },
            new VinculacaoLinha { Rotulo = "15-2", Nome = "BRUNO", Resultado = ResultadosVinculacao.Criar },
            new VinculacaoLinha { Rotulo = "15-1", Nome = "ÁLVARO", Resultado = ResultadosVinculacao.Criar },
            new VinculacaoLinha { Rotulo = "15-4", Nome = "CARLA", Resultado = ResultadosVinculacao.Pendencia, Observacao = "cpf_invalido: CPF vazio; corrigir" },
        ]);

        var linhas = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        linhas.Skip(1).Select(l => l.Split(';')[0]).Should().Equal("15-4", "15-1", "15-2", "15-3");
        linhas[1].Should().EndWith("\"cpf_invalido: CPF vazio; corrigir\"", "texto com ponto e vírgula vai entre aspas");
    }

    [Theory]
    [InlineData("José  da Silva", "JOSE DA SILVA", "Sim")]
    [InlineData("MARIA DA SILVA", "MARIA SILVA", "Não, conferir")]
    [InlineData("MARIA", null, "")]
    public void Names_are_compared_without_accents_case_or_extra_spaces(string nome, string? nomeDp, string esperado) =>
        VinculacaoReport.NomesConferem(nome, nomeDp).Should().Be(esperado);

    [Fact]
    public void Cpf_is_masked() =>
        VinculacaoReport.MascararCpf(TestData.Cpf1).Should().Be("529.***.***-25");

    [Fact]
    public void Job_role_change_and_branch_are_written_and_branch_like_codes_do_not_become_dates()
    {
        var csv = VinculacaoReport.ToCsv(
        [
            new VinculacaoLinha
            {
                Rotulo = "15-00000930",
                Nome = "CLAUDIO",
                Cpf = TestData.Cpf2,
                Filial = 1,
                Resultado = ResultadosVinculacao.Vincular,
                CadastroDp = 2446910,
                NomeDp = "CLAUDIO",
                CodigoExternoDp = "15-1",
                CodigoExternoFica = "15-1",
                CargoRhSenso = "01167 ANALISTA",
                CargoMuda = true,
            },
        ]);

        csv.Split('\n')[1].Should().Be(
            "15-00000930;CLAUDIO;111.***.***-35;1;Vincular pelo CPF;2446910;CLAUDIO;Sim;\"=\"\"15-1\"\"\";\"=\"\"15-1\"\"\";;01167 ANALISTA;Sim;");
    }

    [Fact]
    public void File_name_has_the_company_and_the_local_start_time() =>
        VinculacaoReport.NomeArquivo(15, new DateTimeOffset(2026, 10, 11, 21, 20, 19, TimeSpan.FromHours(-3)))
            .Should().Be("vinculacao-empresa15-20261011-212019.csv");
}
