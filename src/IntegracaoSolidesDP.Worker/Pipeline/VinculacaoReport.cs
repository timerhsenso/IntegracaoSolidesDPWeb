using System.Globalization;
using System.Text;

namespace IntegracaoSolidesDP.Worker.Pipeline;

/// <summary>O que acontece com cada pessoa no relatório de vinculação.</summary>
public static class ResultadosVinculacao
{
    public const string Pendencia = "Pendência";
    public const string Criar = "Criar";
    public const string Vincular = "Vincular pelo CPF";
    public const string Atualizar = "Atualizar (já vinculado)";
    public const string SemAlteracao = "Sem alteração (já vinculado)";
    public const string ForaDoEscopo = "Fora do escopo";

    /// <summary>Ordem do relatório: primeiro o que precisa de decisão do RH.</summary>
    public static readonly IReadOnlyList<string> Ordem = [Pendencia, Criar, Vincular, Atualizar, ForaDoEscopo, SemAlteracao];
}

/// <summary>Uma pessoa do RHSenso no relatório de vinculação, ao lado do cadastro dela no Sólides DP.</summary>
public sealed record VinculacaoLinha
{
    /// <summary>"{empresa}-{matrícula}".</summary>
    public required string Rotulo { get; init; }

    public string? Nome { get; init; }

    /// <summary>CPF só com dígitos; sai mascarado no CSV.</summary>
    public string? Cpf { get; init; }

    public int? Filial { get; init; }

    public required string Resultado { get; init; }

    /// <summary>Id do cadastro no Sólides DP (o que será vinculado, o já vinculado ou o desligado encontrado).</summary>
    public long? CadastroDp { get; init; }

    public string? NomeDp { get; init; }

    public string? CodigoExternoDp { get; init; }

    /// <summary>Código Externo que fica no Sólides DP depois do envio.</summary>
    public string? CodigoExternoFica { get; init; }

    /// <summary>Cargo atual no Sólides DP ("código descrição").</summary>
    public string? CargoDp { get; init; }

    /// <summary>Cargo do RHSenso que será enviado ("código descrição").</summary>
    public string? CargoRhSenso { get; init; }

    public bool? CargoMuda { get; init; }

    public string? Observacao { get; init; }
}

/// <summary>
/// Relatório de vinculação: CSV para o RH do cliente conferir, antes do envio real, o que a integração faria com cada
/// pessoa (vincular a um cadastro que já existe no Sólides DP, criar, pendência, fora do escopo). Gerado em toda
/// simulação; fica na pasta de relatórios do serviço e em solidesdp.run_relatorio (a Web oferece para baixar).
/// </summary>
public static class VinculacaoReport
{
    /// <summary>Tipo em solidesdp.run_relatorio.tipo.</summary>
    public const string Tipo = "vinculacao";

    private const string Cabecalho =
        "Matrícula;Nome no RHSenso;CPF;Filial;Resultado;Cadastro no DP;Nome no DP;Nomes conferem;" +
        "Código Externo no DP;Código Externo que fica;Cargo no DP;Cargo do RHSenso;Muda o cargo;Observação\n";

    public static string NomeArquivo(int cdempresa, DateTimeOffset inicioLocal) =>
        string.Create(CultureInfo.InvariantCulture, $"vinculacao-empresa{cdempresa}-{inicioLocal:yyyyMMdd-HHmmss}.csv");

    public static string ToCsv(IEnumerable<VinculacaoLinha> linhas)
    {
        var csv = new StringBuilder(Cabecalho);
        var ordenadas = linhas
            .OrderBy(l => Posicao(l.Resultado))
            .ThenBy(l => Normalizar(l.Nome), StringComparer.Ordinal)
            .ThenBy(l => l.Rotulo, StringComparer.Ordinal);

        foreach (var l in ordenadas)
        {
            csv.Append(RunReportWriter.Field(l.Rotulo)).Append(';')
                .Append(RunReportWriter.Field(l.Nome)).Append(';')
                .Append(RunReportWriter.Field(MascararCpf(l.Cpf))).Append(';')
                .Append(l.Filial?.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(RunReportWriter.Field(l.Resultado)).Append(';')
                .Append(l.CadastroDp?.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(RunReportWriter.Field(l.NomeDp)).Append(';')
                .Append(RunReportWriter.Field(NomesConferem(l.Nome, l.NomeDp))).Append(';')
                .Append(RunReportWriter.Field(l.CodigoExternoDp)).Append(';')
                .Append(RunReportWriter.Field(l.CodigoExternoFica)).Append(';')
                .Append(RunReportWriter.Field(l.CargoDp)).Append(';')
                .Append(RunReportWriter.Field(l.CargoRhSenso)).Append(';')
                .Append(l.CargoMuda switch { true => "Sim", false => "Não", null => string.Empty }).Append(';')
                .Append(RunReportWriter.Field(l.Observacao)).Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>"Sim" quando os nomes são iguais sem acento, caixa e espaços extras; vazio sem cadastro no DP.</summary>
    public static string NomesConferem(string? nome, string? nomeDp)
    {
        if (string.IsNullOrWhiteSpace(nomeDp))
        {
            return string.Empty;
        }

        return string.Equals(Normalizar(nome), Normalizar(nomeDp), StringComparison.Ordinal) ? "Sim" : "Não, conferir";
    }

    /// <summary>"529.***.***-25": o suficiente para o RH conferir, sem expor o CPF inteiro num arquivo que circula.</summary>
    public static string? MascararCpf(string? cpf) =>
        cpf is { Length: 11 } ? string.Concat(cpf.AsSpan(0, 3), ".***.***-", cpf.AsSpan(9, 2)) : null;

    private static int Posicao(string resultado)
    {
        for (var i = 0; i < ResultadosVinculacao.Ordem.Count; i++)
        {
            if (string.Equals(ResultadosVinculacao.Ordem[i], resultado, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return ResultadosVinculacao.Ordem.Count;
    }

    private static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var semAcento = new StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                semAcento.Append(c);
            }
        }

        return string.Join(' ', semAcento.ToString().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
