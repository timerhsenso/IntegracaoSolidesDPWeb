using Dapper;
using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Criação idempotente das tabelas e das descrições (MS_Description) do schema <c>solidesdp</c>, uma de cada vez.
/// A Web e o serviço (e, no serviço, o sinal de vida, os pedidos e a execução) chamam isso ao mesmo tempo na partida:
/// sem o lock, dois "IF NOT EXISTS" passam juntos e o segundo CREATE TABLE ou sp_addextendedproperty falha
/// (erro 15233, "a propriedade MS_Description já existe").
/// </summary>
public static class CriacaoEsquema
{
    /// <summary>Recurso do sp_getapplock (o mesmo para as tabelas de estado e as da gestão).</summary>
    public const string Recurso = "solidesdp:esquema";

    /// <summary>Roda os lotes numa transação, com lock exclusivo (espera até 2 minutos pelo outro processo).</summary>
    /// <param name="connection">Conexão aberta.</param>
    /// <param name="lotes">Os scripts idempotentes, na ordem.</param>
    /// <param name="ct">Cancelamento.</param>
    public static async Task ExecutarAsync(SqlConnection connection, IReadOnlyList<string> lotes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lotes);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            DECLARE @resultado int;
            EXEC @resultado = sp_getapplock @Resource = @Recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 120000;
            IF @resultado < 0
                THROW 50001, N'Outro processo está criando as tabelas do schema solidesdp há mais de 2 minutos.', 1;
            """, new { Recurso }, transaction, commandTimeout: 180, cancellationToken: ct));

        foreach (var lote in lotes)
        {
            await connection.ExecuteAsync(new CommandDefinition(lote, transaction: transaction, commandTimeout: 180, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }
}
