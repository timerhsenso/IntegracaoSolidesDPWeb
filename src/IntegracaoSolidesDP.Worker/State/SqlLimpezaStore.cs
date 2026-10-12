using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.State;

/// <summary>Apaga o histórico antigo do serviço (limpeza diária do serviço).</summary>
public interface ILimpezaStore
{
    /// <summary>
    /// Apaga até <paramref name="lote"/> execuções iniciadas antes de <paramref name="limite"/>, com os itens e relatórios.
    /// Nunca apaga execução em andamento nem a última de cada empresa (real e simulação). Devolve quantas apagou.
    /// </summary>
    Task<int> ApagarExecucoesAsync(DateTimeOffset limite, int lote, CancellationToken ct);

    /// <summary>Apaga o histórico de e-mails (solidesdp.notificacao_envio) anterior a <paramref name="limite"/>, se a tabela existir.</summary>
    Task<int> ApagarEnviosAsync(DateTimeOffset limite, CancellationToken ct);
}

public sealed class SqlLimpezaStore(ConnectionFactory connections) : ILimpezaStore
{
    public async Task<int> ApagarExecucoesAsync(DateTimeOffset limite, int lote, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SET NOCOUNT ON;
            DECLARE @ids TABLE (run_id uniqueidentifier NOT NULL PRIMARY KEY);

            WITH ultimas AS (
                SELECT run_id,
                       ROW_NUMBER() OVER (PARTITION BY instance_name, cdempresa, dry_run ORDER BY started_at DESC) AS ordem
                FROM solidesdp.runs
            )
            INSERT INTO @ids (run_id)
            SELECT TOP (@Lote) r.run_id
            FROM solidesdp.runs r
            JOIN ultimas u ON u.run_id = r.run_id
            WHERE r.started_at < @Limite AND r.status <> 'running' AND u.ordem > 1
            ORDER BY r.started_at;

            DELETE i FROM solidesdp.run_items i JOIN @ids d ON d.run_id = i.run_id;
            DELETE rr FROM solidesdp.run_relatorio rr JOIN @ids d ON d.run_id = rr.run_id;
            DELETE r FROM solidesdp.runs r JOIN @ids d ON d.run_id = r.run_id;

            SELECT COUNT(*) FROM @ids;
            """, new { Limite = limite, Lote = lote }, commandTimeout: 300, cancellationToken: ct));
    }

    public async Task<int> ApagarEnviosAsync(DateTimeOffset limite, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SET NOCOUNT ON;
            DECLARE @apagados int = 0;
            IF OBJECT_ID(N'solidesdp.notificacao_envio', N'U') IS NOT NULL
            BEGIN
                DELETE FROM solidesdp.notificacao_envio WHERE enviado_em < @Limite;
                SET @apagados = @@ROWCOUNT;
            END;
            SELECT @apagados;
            """, new { Limite = limite }, commandTimeout: 300, cancellationToken: ct));
    }
}
