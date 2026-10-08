using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Worker.Source;

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
