using System.Globalization;
using System.Text;
using System.Text.Json;

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

    /// <summary>Já vinculado e com demissão ou transferência no RHSenso: a integração desliga no Sólides DP.</summary>
    public const string Desligar = "Desligar";

    /// <summary>
    /// Ativo no Sólides DP sem colaborador ativo correspondente no RHSenso. A integração não mexe nesse cadastro
    /// (só desliga quem ela vinculou): o RH decide se desliga no DP ou corrige o RHSenso.
    /// </summary>
    public const string SoNoDp = "Só no Sólides DP";

    /// <summary>Ordem do relatório: primeiro o que precisa de decisão do RH.</summary>
    public static readonly IReadOnlyList<string> Ordem = [Pendencia, SoNoDp, Desligar, Criar, Vincular, Atualizar, ForaDoEscopo, SemAlteracao];
}

/// <summary>Uma pessoa do RHSenso no relatório de vinculação, ao lado do cadastro dela no Sólides DP.</summary>
public sealed record VinculacaoLinha
{
    /// <summary>"{empresa}-{matrícula}"; vazio quando a pessoa só existe no Sólides DP.</summary>
    public required string Rotulo { get; init; }

    /// <summary>Nome no RHSenso (vazio quando a pessoa só existe no Sólides DP).</summary>
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
/// pessoa (vincular a um cadastro que já existe no Sólides DP, criar, desligar, pendência, fora do escopo) e quem está
/// ativo no Sólides DP sem colaborador ativo no RHSenso ("Só no Sólides DP", só com token). Gerado em toda
/// simulação; fica na pasta de relatórios do serviço e em solidesdp.run_relatorio (a Web oferece para baixar).
/// </summary>
public static class VinculacaoReport
{
    /// <summary>Tipo em solidesdp.run_relatorio.tipo: o CSV para baixar.</summary>
    public const string Tipo = "vinculacao";

    /// <summary>Tipo em solidesdp.run_relatorio.tipo: as mesmas linhas em JSON, para a tela da Web.</summary>
    public const string TipoLinhas = "vinculacao-linhas";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string Cabecalho =
        "Matrícula;Nome no RHSenso;CPF;Filial;Resultado;Cadastro no DP;Nome no DP;Nomes conferem;" +
        "Código Externo no DP;Código Externo que fica;Cargo no DP;Cargo do RHSenso;Muda o cargo;Observação\n";

    public static string NomeArquivo(int cdempresa, DateTimeOffset inicioLocal) =>
        string.Create(CultureInfo.InvariantCulture, $"vinculacao-empresa{cdempresa}-{inicioLocal:yyyyMMdd-HHmmss}.csv");

    /// <summary>O mesmo nome do CSV, com .json.</summary>
    public static string NomeArquivoLinhas(int cdempresa, DateTimeOffset inicioLocal) =>
        Path.ChangeExtension(NomeArquivo(cdempresa, inicioLocal), ".json");

    /// <summary>
    /// As linhas do CSV, na mesma ordem e já com o CPF mascarado e a conferência de nomes, para a tela de vinculação da Web.
    /// Chaves em camelCase: matricula, nome, cpf, filial, resultado, cadastroDp, nomeDp, nomesConferem, codigoExternoDp,
    /// codigoExternoFica, cargoDp, cargoRhSenso, cargoMuda, observacao.
    /// </summary>
    public static string ToJson(IEnumerable<VinculacaoLinha> linhas) =>
        JsonSerializer.Serialize(
            Ordenar(linhas).Select(l => new LinhaJson(
                l.Rotulo, l.Nome, MascararCpf(l.Cpf), l.Filial, l.Resultado, l.CadastroDp, l.NomeDp, NomesConferem(l.Nome, l.NomeDp),
                l.CodigoExternoDp, l.CodigoExternoFica, l.CargoDp, l.CargoRhSenso, l.CargoMuda, l.Observacao)),
            JsonOptions);

    public static string ToCsv(IEnumerable<VinculacaoLinha> linhas)
    {
        var csv = new StringBuilder(Cabecalho);
        foreach (var l in Ordenar(linhas))
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

    /// <summary>Primeiro o que precisa de decisão do RH; dentro de cada resultado, pelo nome (do RHSenso ou, sem ele, do DP).</summary>
    private static IEnumerable<VinculacaoLinha> Ordenar(IEnumerable<VinculacaoLinha> linhas) =>
        linhas
            .OrderBy(l => Posicao(l.Resultado))
            .ThenBy(l => Normalizar(l.Nome ?? l.NomeDp), StringComparer.Ordinal)
            .ThenBy(l => l.Rotulo, StringComparer.Ordinal);

    private sealed record LinhaJson(
        string Matricula, string? Nome, string? Cpf, int? Filial, string Resultado, long? CadastroDp, string? NomeDp, string NomesConferem,
        string? CodigoExternoDp, string? CodigoExternoFica, string? CargoDp, string? CargoRhSenso, bool? CargoMuda, string? Observacao);

    /// <summary>"Sim" quando os nomes são iguais sem acento, caixa e espaços extras; vazio se faltar um dos dois.</summary>
    public static string NomesConferem(string? nome, string? nomeDp)
    {
        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(nomeDp))
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
