using Dapper;
using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Worker.Source;

public interface ISourceReader
{
    Task<IReadOnlyList<EmployeeRow>> ReadEmployeesAsync(IReadOnlyCollection<int> tipos, IReadOnlyCollection<int> empresas, CancellationToken ct);

    Task<IReadOnlyList<JobRoleRow>> ReadJobRolesAsync(IReadOnlyCollection<string> codigos, CancellationToken ct);

    Task<IReadOnlyList<WorkplaceRow>> ReadWorkplacesAsync(CancellationToken ct);

    Task<IReadOnlyList<VacationRow>> ReadVacationsEndingFromAsync(DateOnly desde, CancellationToken ct);

    /// <summary>Quais destes feria2.id ainda existem (para distinguir linha apagada de linha fora da janela).</summary>
    Task<IReadOnlySet<Guid>> ExistingVacationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

/// <summary>Leitura somente-leitura das tabelas do RHSenso (schema dbo).</summary>
public sealed class SqlSourceReader(ConnectionFactory connections) : ISourceReader
{
    private const string EmployeesSql = """
        SELECT
            f.id                       AS Id,
            RTRIM(f.nomatric)          AS Nomatric,
            f.cdempresa                AS Cdempresa,
            f.cdfilial                 AS Cdfilial,
            RTRIM(f.nmcolab)           AS Nome,
            f.tpcolab                  AS TipoColaborador,
            RTRIM(f.cdsituacao)        AS Situacao,
            CAST(CASE WHEN s.fldemissao = 'S' THEN 1 ELSE 0 END AS bit) AS SituacaoDeDesligamento,
            f.dtadmissao               AS DataAdmissao,
            f.dtdemissao               AS DataDemissao,
            f.dttransf                 AS DataTransferencia,
            RTRIM(f.cdcausres)         AS CausaRescisao,
            RTRIM(f.nocpf)             AS Cpf,
            RTRIM(f.nopis)             AS Pis,
            RTRIM(f.nocartprof)        AS Ctps,
            RTRIM(f.noserie)           AS SerieCtps,
            f.dtnasc                   AS DataNascimento,
            RTRIM(f.cdsexo)            AS Sexo,
            RTRIM(f.cdestcivil)        AS EstadoCivil,
            RTRIM(f.cdinstruc)         AS GrauInstrucao,
            f.cod_raca                 AS Raca,
            RTRIM(f.dcemail)           AS Email,
            RTRIM(f.emailalternativo)  AS EmailAlternativo,
            RTRIM(f.noddd)             AS Ddd,
            RTRIM(f.notelefone)        AS Telefone,
            RTRIM(f.nmmaecolab)        AS NomeMae,
            RTRIM(f.nmpaicolab)        AS NomePai,
            RTRIM(f.cdcargo)           AS Cargo,
            RTRIM(f.cdccusto)          AS CentroCusto,
            RTRIM(c.dcccusto)          AS CentroCustoDescricao,
            RTRIM(t.cdcgc)             AS Cnpj
        FROM dbo.func1 f
        LEFT JOIN dbo.tsitu1 s ON s.cdsituacao = f.cdsituacao
        LEFT JOIN dbo.tcus1 c  ON c.cdccusto = f.cdccusto
        LEFT JOIN dbo.test1 t  ON t.cdempresa = f.cdempresa AND t.cdfilial = f.cdfilial
        WHERE f.tpcolab IN @Tipos
          AND (@TodasEmpresas = 1 OR f.cdempresa IN @Empresas)
        """;

    private const string JobRolesSql = """
        SELECT RTRIM(cdcargo) AS Cdcargo, RTRIM(dccargo) AS Descricao, RTRIM(cdcbo6) AS Cbo
        FROM dbo.cargo1
        WHERE RTRIM(cdcargo) IN @Codigos
        """;

    private const string WorkplacesSql = """
        SELECT cdempresa AS Cdempresa, cdfilial AS Cdfilial, RTRIM(nmfantasia) AS NomeFantasia,
               RTRIM(dcestab) AS Descricao, RTRIM(cdcgc) AS Cnpj
        FROM dbo.test1
        """;

    private const string VacationsSql = """
        SELECT id AS Id, RTRIM(nomatric) AS Nomatric, cdempresa AS Cdempresa, cdfilial AS Cdfilial,
               dtinipf AS Inicio, dtfimpf AS Fim, qtdiasfe AS Dias, qtabono AS Abono, ISNULL(flconfirm, 0) AS Situacao
        FROM dbo.feria2
        WHERE dtfimpf >= @Desde AND dtinipf IS NOT NULL AND dtfimpf IS NOT NULL
        """;

    public async Task<IReadOnlyList<EmployeeRow>> ReadEmployeesAsync(
        IReadOnlyCollection<int> tipos, IReadOnlyCollection<int> empresas, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EmployeeRow>(new CommandDefinition(
            EmployeesSql,
            new { Tipos = tipos.ToArray(), TodasEmpresas = empresas.Count == 0, Empresas = empresas.Count == 0 ? new[] { 0 } : empresas.ToArray() },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<JobRoleRow>> ReadJobRolesAsync(IReadOnlyCollection<string> codigos, CancellationToken ct)
    {
        if (codigos.Count == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<JobRoleRow>(new CommandDefinition(JobRolesSql, new { Codigos = codigos }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<WorkplaceRow>> ReadWorkplacesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<WorkplaceRow>(new CommandDefinition(WorkplacesSql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<VacationRow>> ReadVacationsEndingFromAsync(DateOnly desde, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VacationRow>(new CommandDefinition(
            VacationsSql, new { Desde = desde.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlySet<Guid>> ExistingVacationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var connection = await connections.OpenAsync(ct);
        var found = new HashSet<Guid>();
        // SQL Server aceita no máximo ~2100 parâmetros por comando.
        foreach (var chunk in ids.Chunk(1000))
        {
            var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM dbo.feria2 WHERE id IN @Ids", new { Ids = chunk }, cancellationToken: ct));
            found.UnionWith(rows);
        }

        return found;
    }
}

/// <summary>Abre conexões com o banco do RHSenso (ConnectionStrings:Rhu).</summary>
public sealed class ConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
