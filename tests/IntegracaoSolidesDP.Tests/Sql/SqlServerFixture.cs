using Dapper;
using IntegracaoSolidesDP.Worker.Source;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace IntegracaoSolidesDP.Tests.Sql;

/// <summary>
/// SQL Server real (Testcontainers) com as tabelas do RHSenso usadas pelo worker. Os tipos
/// das colunas são os do bd_rhu_adn (char com padding, datetime, varchar(11) do CPF etc.).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Database = "bd_rhu_adn";

    /// <summary>Tabelas do RHSenso com os tipos reais (compartilhado com os smoke tests do CI).</summary>
    private static readonly string RhuSchema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sql", "rhu-schema.sql"));

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public ConnectionFactory Connections => new(ConnectionString);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using (var master = new SqlConnection(_container.GetConnectionString()))
        {
            await master.ExecuteAsync($"CREATE DATABASE {Database}");
        }

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = Database }.ConnectionString;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync(RhuSchema);
    }

    /// <summary>Apaga os dados do RHSenso e o schema de estado entre testes.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM dbo.feria2; DELETE FROM dbo.func1; DELETE FROM dbo.test1; DELETE FROM dbo.cargo1; DELETE FROM dbo.tcus1;
            IF OBJECT_ID('solidesdp.run_items') IS NOT NULL DROP TABLE solidesdp.run_items;
            IF OBJECT_ID('solidesdp.runs') IS NOT NULL DROP TABLE solidesdp.runs;
            IF OBJECT_ID('solidesdp.entity_state') IS NOT NULL DROP TABLE solidesdp.entity_state;
            IF OBJECT_ID('solidesdp.vacation_state') IS NOT NULL DROP TABLE solidesdp.vacation_state;
            IF OBJECT_ID('solidesdp.meta') IS NOT NULL DROP TABLE solidesdp.meta;
            """);
    }

    public async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync(sql, parameters);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return (await connection.QueryAsync<T>(sql, parameters)).AsList();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}
