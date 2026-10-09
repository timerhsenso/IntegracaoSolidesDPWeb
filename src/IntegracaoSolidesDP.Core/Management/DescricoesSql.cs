using System.Text;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Gera o T-SQL idempotente que documenta tabelas e colunas do schema <c>solidesdp</c> em
/// extended properties (MS_Description), visíveis no SSMS e em qualquer ferramenta de dicionário de dados.
/// </summary>
public static class DescricoesSql
{
    /// <param name="itens">(tabela, coluna ou null para a própria tabela, descrição). Textos fixos do código.</param>
    public static string Gerar(IEnumerable<(string Tabela, string? Coluna, string Descricao)> itens)
    {
        var sql = new StringBuilder();
        foreach (var (tabela, coluna, descricao) in itens)
        {
            var objeto = $"N'solidesdp.{Escapar(tabela)}'";
            var minor = coluna is null ? "0" : $"COLUMNPROPERTY(OBJECT_ID({objeto}), N'{Escapar(coluna)}', 'ColumnId')";
            var existe = coluna is null
                ? $"OBJECT_ID({objeto}) IS NOT NULL"
                : $"COL_LENGTH({objeto}, N'{Escapar(coluna)}') IS NOT NULL";
            var nivel2 = coluna is null ? string.Empty : $", @level2type = N'COLUMN', @level2name = N'{Escapar(coluna)}'";

            sql.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"""
                IF {existe} AND NOT EXISTS (
                    SELECT 1 FROM sys.extended_properties
                    WHERE major_id = OBJECT_ID({objeto}) AND minor_id = {minor} AND name = N'MS_Description')
                    EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'{Escapar(descricao)}',
                        @level0type = N'SCHEMA', @level0name = N'solidesdp', @level1type = N'TABLE', @level1name = N'{Escapar(tabela)}'{nivel2};
                """);
        }

        return sql.ToString();
    }

    private static string Escapar(string texto) => texto.Replace("'", "''", StringComparison.Ordinal);
}
