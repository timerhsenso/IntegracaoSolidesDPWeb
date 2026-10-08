using Dapper;
using IntegracaoSolidesDP.Worker.Source;
using Microsoft.Data.SqlClient;

namespace IntegracaoSolidesDP.Worker.Management;

public interface IManagementStore
{
    /// <summary>
    /// Cria as tabelas da gestão (idempotente). Pode rodar antes do serviço (a Web chama no start):
    /// as colunas novas de solidesdp.runs entram quando a tabela existir.
    /// </summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    Task<ConfigurationVersion?> GetCurrentConfigurationAsync(string instanceName, CancellationToken ct);

    /// <summary>Grava a versão 1 se a instância ainda não tiver configuração. Concorrência segura.</summary>
    Task SeedConfigurationAsync(string instanceName, string syncJson, CancellationToken ct);

    /// <summary>Acrescenta uma versão (nunca altera as anteriores). Devolve o número da nova versão.</summary>
    Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, string? note, string createdBy, CancellationToken ct);

    Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, CancellationToken ct);

    /// <summary>Retira o comando pendente mais antigo da instância (pendente → executando). Null se não houver.</summary>
    Task<ClaimedCommand?> ClaimNextCommandAsync(string instanceName, CancellationToken ct);

    Task CompleteCommandAsync(long commandId, string status, Guid? runId, string? result, CancellationToken ct);

    /// <summary>Comandos "executando" de um serviço que parou no meio: viram "falhou". Devolve quantos.</summary>
    Task<int> FailInterruptedCommandsAsync(string instanceName, CancellationToken ct);

    /// <summary>Grava em solidesdp.runs quem pediu a execução e a versão da configuração usada.</summary>
    Task TagRunAsync(Guid runId, string? requestedBy, int? configVersion, CancellationToken ct);
}

/// <summary>
/// Tabelas da gestão pela Web, no mesmo schema <c>solidesdp</c>. Nada aqui é apagado:
/// configuração é versionada (só INSERT) e comandos/auditoria são histórico.
/// </summary>
public sealed class SqlManagementStore(ConnectionFactory connections, TimeProvider clock) : IManagementStore
{
    private const string SchemaSql = """
        IF SCHEMA_ID(N'solidesdp') IS NULL EXEC(N'CREATE SCHEMA solidesdp');

        IF OBJECT_ID(N'solidesdp.configuracao', N'U') IS NULL
        CREATE TABLE solidesdp.configuracao (
            id             int IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_configuracao PRIMARY KEY,
            instance_name  nvarchar(64)      NOT NULL,
            versao         int               NOT NULL,
            ativo          bit               NOT NULL,
            sync_json      nvarchar(max)     NOT NULL,
            observacao     nvarchar(500)     NULL,
            criado_por     nvarchar(64)      NOT NULL,
            criado_em      datetimeoffset(7) NOT NULL,
            CONSTRAINT uq_solidesdp_configuracao UNIQUE (instance_name, versao)
        );

        IF OBJECT_ID(N'solidesdp.comando', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.comando (
                id             bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_comando PRIMARY KEY,
                instance_name  nvarchar(64)      NOT NULL,
                tipo           nvarchar(32)      NOT NULL,
                status         nvarchar(16)      NOT NULL,
                solicitado_por nvarchar(64)      NOT NULL,
                solicitado_em  datetimeoffset(7) NOT NULL,
                iniciado_em    datetimeoffset(7) NULL,
                concluido_em   datetimeoffset(7) NULL,
                run_id         uniqueidentifier  NULL, -- solidesdp.runs.run_id (sem FK: a fila pode existir antes de runs)
                resultado      nvarchar(max)     NULL
            );
            CREATE INDEX ix_solidesdp_comando_fila ON solidesdp.comando(instance_name, status, id);
        END;

        IF OBJECT_ID(N'solidesdp.auditoria', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.auditoria (
                id           bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_auditoria PRIMARY KEY,
                usuario      nvarchar(64)      NOT NULL,
                acao         nvarchar(64)      NOT NULL,
                detalhe      nvarchar(max)     NULL,
                ip           varchar(45)       NULL,
                ocorrido_em  datetimeoffset(7) NOT NULL
            );
            CREATE INDEX ix_solidesdp_auditoria_data ON solidesdp.auditoria(ocorrido_em);
        END;

        IF OBJECT_ID(N'solidesdp.runs', N'U') IS NOT NULL AND COL_LENGTH(N'solidesdp.runs', N'solicitado_por') IS NULL
            ALTER TABLE solidesdp.runs ADD solicitado_por nvarchar(64) NULL;

        IF OBJECT_ID(N'solidesdp.runs', N'U') IS NOT NULL AND COL_LENGTH(N'solidesdp.runs', N'config_versao') IS NULL
            ALTER TABLE solidesdp.runs ADD config_versao int NULL;
        """;

    private const int UniqueKeyViolation = 2627;
    private const int DuplicateKeyRow = 2601;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
    }

    public async Task<ConfigurationVersion?> GetCurrentConfigurationAsync(string instanceName, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ConfigurationVersion>(new CommandDefinition("""
            SELECT TOP (1) versao AS Version, ativo AS Active, sync_json AS SyncJson, observacao AS Note,
                   criado_por AS CreatedBy, criado_em AS CreatedAt
            FROM solidesdp.configuracao
            WHERE instance_name = @InstanceName
            ORDER BY versao DESC
            """, new { InstanceName = instanceName }, cancellationToken: ct));
    }

    public async Task SeedConfigurationAsync(string instanceName, string syncJson, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.configuracao (instance_name, versao, ativo, sync_json, observacao, criado_por, criado_em)
                SELECT @InstanceName, 1, 1, @SyncJson, N'Versão inicial criada a partir do appsettings.json', @CreatedBy, @Now
                WHERE NOT EXISTS (
                    SELECT 1 FROM solidesdp.configuracao WITH (UPDLOCK, HOLDLOCK) WHERE instance_name = @InstanceName);
                """,
                new { InstanceName = instanceName, SyncJson = syncJson, CreatedBy = ManagementUsers.System, Now = clock.GetUtcNow() },
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is UniqueKeyViolation or DuplicateKeyRow)
        {
            // Outro processo gravou a versão 1 ao mesmo tempo: o resultado é o mesmo.
        }
    }

    public async Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, string? note, string createdBy, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO solidesdp.configuracao (instance_name, versao, ativo, sync_json, observacao, criado_por, criado_em)
            OUTPUT inserted.versao
            SELECT @InstanceName, ISNULL(MAX(versao), 0) + 1, @Active, @SyncJson, @Note, @CreatedBy, @Now
            FROM solidesdp.configuracao WITH (UPDLOCK, HOLDLOCK)
            WHERE instance_name = @InstanceName;
            """,
            new { InstanceName = instanceName, Active = active, SyncJson = syncJson, Note = note, CreatedBy = createdBy, Now = clock.GetUtcNow() },
            transaction,
            cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return version;
    }

    public async Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, CancellationToken ct)
    {
        if (!CommandTypes.All.Contains(type, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de comando desconhecido.");
        }

        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO solidesdp.comando (instance_name, tipo, status, solicitado_por, solicitado_em)
            OUTPUT inserted.id
            VALUES (@InstanceName, @Type, @Status, @RequestedBy, @Now);
            """,
            new { InstanceName = instanceName, Type = type, Status = CommandStatuses.Pending, RequestedBy = requestedBy, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task<ClaimedCommand?> ClaimNextCommandAsync(string instanceName, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ClaimedCommand>(new CommandDefinition("""
            WITH proximo AS (
                SELECT TOP (1) status, iniciado_em, id, tipo, solicitado_por
                FROM solidesdp.comando WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE instance_name = @InstanceName AND status = @Pending
                ORDER BY id
            )
            UPDATE proximo SET status = @Running, iniciado_em = @Now
            OUTPUT inserted.id AS Id, inserted.tipo AS Type, inserted.solicitado_por AS RequestedBy;
            """,
            new { InstanceName = instanceName, Pending = CommandStatuses.Pending, Running = CommandStatuses.Running, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task CompleteCommandAsync(long commandId, string status, Guid? runId, string? result, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.comando
            SET status = @Status, concluido_em = @Now, run_id = @RunId, resultado = @Result
            WHERE id = @Id
            """,
            new { Id = commandId, Status = status, RunId = runId, Result = result, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task<int> FailInterruptedCommandsAsync(string instanceName, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.comando
            SET status = @Failed, concluido_em = @Now, resultado = N'interrompido: o serviço foi reiniciado durante a execução'
            WHERE instance_name = @InstanceName AND status = @Running
            """,
            new { InstanceName = instanceName, Failed = CommandStatuses.Failed, Running = CommandStatuses.Running, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task TagRunAsync(Guid runId, string? requestedBy, int? configVersion, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.runs SET solicitado_por = @RequestedBy, config_versao = @ConfigVersion WHERE run_id = @RunId
            """,
            new { RunId = runId, RequestedBy = requestedBy, ConfigVersion = configVersion },
            cancellationToken: ct));
    }
}
