using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Último sinal de vida de um serviço (instância + máquina), gravado de tempos em tempos enquanto ele roda.</summary>
public sealed record SinalServico
{
    public required string Instancia { get; init; }
    public required string Maquina { get; init; }
    public string? Versao { get; init; }
    public int? Pid { get; init; }

    /// <summary>Quando o processo atual do serviço começou.</summary>
    public DateTimeOffset IniciadoEm { get; init; }

    public DateTimeOffset UltimoSinal { get; init; }
}

/// <summary>Sinal de vida do serviço (solidesdp.servico_sinal, criada pelo serviço no SqlManagementStore).</summary>
public interface ISinalServicoStore
{
    Task RegistrarAsync(SinalServico sinal, CancellationToken ct);

    /// <summary>Todos os serviços que já deram sinal, o mais recente primeiro. Vazio se a tabela ainda não existe.</summary>
    Task<IReadOnlyList<SinalServico>> ListarAsync(CancellationToken ct);

    /// <summary>Apaga os sinais de serviços que não aparecem desde <paramref name="limite"/> (máquina desativada). Devolve quantos.</summary>
    Task<int> ApagarAntigosAsync(DateTimeOffset limite, CancellationToken ct);
}

public sealed class SqlSinalServicoStore(ConnectionFactory connections) : ISinalServicoStore
{
    public async Task RegistrarAsync(SinalServico sinal, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE solidesdp.servico_sinal WITH (HOLDLOCK) AS target
            USING (SELECT @Instancia AS instance_name, @Maquina AS maquina) AS source
               ON target.instance_name = source.instance_name AND target.maquina = source.maquina
            WHEN MATCHED THEN UPDATE SET
                versao = @Versao, pid = @Pid, iniciado_em = @IniciadoEm, ultimo_sinal = @UltimoSinal
            WHEN NOT MATCHED THEN
                INSERT (instance_name, maquina, versao, pid, iniciado_em, ultimo_sinal)
                VALUES (@Instancia, @Maquina, @Versao, @Pid, @IniciadoEm, @UltimoSinal);
            """, sinal, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SinalServico>> ListarAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<SinalServico>(new CommandDefinition("""
            IF OBJECT_ID(N'solidesdp.servico_sinal', N'U') IS NOT NULL
                SELECT instance_name AS Instancia, maquina AS Maquina, versao AS Versao, pid AS Pid,
                       iniciado_em AS IniciadoEm, ultimo_sinal AS UltimoSinal
                FROM solidesdp.servico_sinal
                ORDER BY ultimo_sinal DESC;
            ELSE
                SELECT CAST(NULL AS nvarchar(64)) AS Instancia WHERE 1 = 0;
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> ApagarAntigosAsync(DateTimeOffset limite, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM solidesdp.servico_sinal WHERE ultimo_sinal < @Limite",
            new { Limite = limite }, cancellationToken: ct));
    }
}
