using System.Data;
using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.State;

public interface IStateStore
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Lock exclusivo por instância (sp_getapplock). Null se outra execução estiver em andamento.</summary>
    Task<IAsyncDisposable?> TryAcquireRunLockAsync(string instanceName, CancellationToken ct);

    /// <summary>Grava o esquema da chave do colaborador na primeira execução e recusa se mudar depois.</summary>
    Task VerifyKeySchemeAsync(string scheme, CancellationToken ct);

    Task<Guid> StartRunAsync(string instanceName, bool dryRun, string trigger, CancellationToken ct);

    Task FinishRunAsync(Guid runId, string status, string summaryJson, string? errorMessage, CancellationToken ct);

    Task AddItemsAsync(Guid runId, IReadOnlyCollection<RunItem> items, CancellationToken ct);

    Task<Dictionary<string, EntityState>> LoadEntityStatesAsync(string entityType, CancellationToken ct);

    Task UpsertEntityStateAsync(EntityState state, CancellationToken ct);

    Task<Dictionary<Guid, VacationState>> LoadVacationStatesAsync(CancellationToken ct);

    Task UpsertVacationStateAsync(VacationState state, CancellationToken ct);
}

/// <summary>
/// Estado da integração no próprio bd_rhu_adn, num schema separado (<c>solidesdp</c>):
/// o login do worker precisa só de leitura em <c>dbo</c> e DDL/DML em <c>solidesdp</c>.
/// </summary>
public sealed class SqlStateStore(ConnectionFactory connections, TimeProvider clock) : IStateStore
{
    private const string SchemaSql = """
        IF SCHEMA_ID(N'solidesdp') IS NULL EXEC(N'CREATE SCHEMA solidesdp');

        IF OBJECT_ID(N'solidesdp.meta', N'U') IS NULL
        CREATE TABLE solidesdp.meta (
            [key]   nvarchar(64)  NOT NULL CONSTRAINT pk_solidesdp_meta PRIMARY KEY,
            [value] nvarchar(max) NOT NULL
        );

        IF OBJECT_ID(N'solidesdp.runs', N'U') IS NULL
        CREATE TABLE solidesdp.runs (
            run_id        uniqueidentifier  NOT NULL CONSTRAINT pk_solidesdp_runs PRIMARY KEY,
            instance_name nvarchar(64)      NOT NULL,
            dry_run       bit               NOT NULL,
            triggered_by  nvarchar(32)      NOT NULL,
            status        nvarchar(32)      NOT NULL,
            started_at    datetimeoffset(7) NOT NULL,
            finished_at   datetimeoffset(7) NULL,
            summary_json  nvarchar(max)     NULL,
            error_message nvarchar(max)     NULL
        );

        IF OBJECT_ID(N'solidesdp.run_items', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.run_items (
                id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_run_items PRIMARY KEY,
                run_id      uniqueidentifier  NOT NULL CONSTRAINT fk_solidesdp_run_items_run REFERENCES solidesdp.runs(run_id),
                entity_type nvarchar(32)      NOT NULL,
                external_id nvarchar(128)     NOT NULL,
                action      nvarchar(32)      NOT NULL,
                status      nvarchar(32)      NOT NULL,
                http_status int               NULL,
                message     nvarchar(max)     NULL,
                created_at  datetimeoffset(7) NOT NULL
            );
            CREATE INDEX ix_solidesdp_run_items_run ON solidesdp.run_items(run_id, id);
        END;

        IF OBJECT_ID(N'solidesdp.entity_state', N'U') IS NULL
        CREATE TABLE solidesdp.entity_state (
            entity_type  nvarchar(32)      NOT NULL,
            external_id  nvarchar(128)     NOT NULL,
            remote_id    bigint            NULL,
            payload_hash char(64)          NULL,
            status       nvarchar(32)      NOT NULL,
            extra_json   nvarchar(max)     NULL,
            updated_at   datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_solidesdp_entity_state PRIMARY KEY (entity_type, external_id)
        );

        IF OBJECT_ID(N'solidesdp.vacation_state', N'U') IS NULL
        CREATE TABLE solidesdp.vacation_state (
            feria2_id            uniqueidentifier  NOT NULL CONSTRAINT pk_solidesdp_vacation_state PRIMARY KEY,
            employee_external_id nvarchar(128)     NOT NULL,
            remote_adjustment_id bigint            NULL,
            payload_hash         char(64)          NULL,
            status               nvarchar(32)      NOT NULL,
            attempts             int               NOT NULL CONSTRAINT df_solidesdp_vacation_attempts DEFAULT 0,
            last_error           nvarchar(max)     NULL,
            start_date           bigint            NULL,
            end_date             bigint            NULL,
            updated_at           datetimeoffset(7) NOT NULL
        );
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
    }

    public async Task<IAsyncDisposable?> TryAcquireRunLockAsync(string instanceName, CancellationToken ct)
    {
        var connection = await connections.OpenAsync(ct);
        try
        {
            var parameters = new DynamicParameters();
            parameters.Add("@Resource", $"solidesdp:{instanceName}");
            parameters.Add("@LockMode", "Exclusive");
            parameters.Add("@LockOwner", "Session");
            parameters.Add("@LockTimeout", 0);
            parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
            await connection.ExecuteAsync(new CommandDefinition(
                "sp_getapplock", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            if (parameters.Get<int>("@Result") < 0)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new RunLock(connection, $"solidesdp:{instanceName}");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task VerifyKeySchemeAsync(string scheme, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT [value] FROM solidesdp.meta WHERE [key] = N'employee_key_scheme'", cancellationToken: ct));

        if (current is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO solidesdp.meta ([key], [value]) VALUES (N'employee_key_scheme', @Scheme)",
                new { Scheme = scheme }, cancellationToken: ct));
        }
        else if (!string.Equals(current, scheme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"O estado foi gravado com a chave de colaborador '{current}', mas este build usa '{scheme}'. " +
                "Trocar a chave depois do go-live duplicaria colaboradores no Sólides DP.");
        }
    }

    public async Task<Guid> StartRunAsync(string instanceName, bool dryRun, string trigger, CancellationToken ct)
    {
        var runId = Guid.CreateVersion7();
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.runs (run_id, instance_name, dry_run, triggered_by, status, started_at)
            VALUES (@RunId, @InstanceName, @DryRun, @Trigger, @Status, @Now)
            """,
            new { RunId = runId, InstanceName = instanceName, DryRun = dryRun, Trigger = trigger, Status = RunStatuses.Running, Now = clock.GetUtcNow() },
            cancellationToken: ct));
        return runId;
    }

    public async Task FinishRunAsync(Guid runId, string status, string summaryJson, string? errorMessage, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.runs
            SET status = @Status, finished_at = @Now, summary_json = @Summary, error_message = @Error
            WHERE run_id = @RunId
            """,
            new { RunId = runId, Status = status, Now = clock.GetUtcNow(), Summary = summaryJson, Error = errorMessage },
            cancellationToken: ct));
    }

    public async Task AddItemsAsync(Guid runId, IReadOnlyCollection<RunItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.run_items (run_id, entity_type, external_id, action, status, http_status, message, created_at)
            VALUES (@RunId, @EntityType, @ExternalId, @Action, @Status, @HttpStatus, @Message, @CreatedAt)
            """,
            items.Select(i => new { RunId = runId, i.EntityType, i.ExternalId, i.Action, i.Status, i.HttpStatus, i.Message, CreatedAt = now }),
            transaction,
            cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<Dictionary<string, EntityState>> LoadEntityStatesAsync(string entityType, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EntityState>(new CommandDefinition("""
            SELECT entity_type AS EntityType, external_id AS ExternalId, remote_id AS RemoteId,
                   payload_hash AS PayloadHash, status AS Status, extra_json AS ExtraJson, updated_at AS UpdatedAt
            FROM solidesdp.entity_state
            WHERE entity_type = @EntityType
            """, new { EntityType = entityType }, cancellationToken: ct));
        return rows.ToDictionary(r => r.ExternalId, StringComparer.Ordinal);
    }

    public async Task UpsertEntityStateAsync(EntityState state, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE solidesdp.entity_state WITH (HOLDLOCK) AS target
            USING (SELECT @EntityType AS entity_type, @ExternalId AS external_id) AS source
               ON target.entity_type = source.entity_type AND target.external_id = source.external_id
            WHEN MATCHED THEN UPDATE SET
                remote_id = @RemoteId, payload_hash = @PayloadHash, status = @Status, extra_json = @ExtraJson, updated_at = @Now
            WHEN NOT MATCHED THEN
                INSERT (entity_type, external_id, remote_id, payload_hash, status, extra_json, updated_at)
                VALUES (@EntityType, @ExternalId, @RemoteId, @PayloadHash, @Status, @ExtraJson, @Now);
            """,
            new { state.EntityType, state.ExternalId, state.RemoteId, state.PayloadHash, state.Status, state.ExtraJson, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task<Dictionary<Guid, VacationState>> LoadVacationStatesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VacationState>(new CommandDefinition("""
            SELECT feria2_id AS Feria2Id, employee_external_id AS EmployeeExternalId, remote_adjustment_id AS RemoteAdjustmentId,
                   payload_hash AS PayloadHash, status AS Status, attempts AS Attempts, last_error AS LastError,
                   start_date AS StartDate, end_date AS EndDate, updated_at AS UpdatedAt
            FROM solidesdp.vacation_state
            """, cancellationToken: ct));
        return rows.ToDictionary(r => r.Feria2Id);
    }

    public async Task UpsertVacationStateAsync(VacationState state, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE solidesdp.vacation_state WITH (HOLDLOCK) AS target
            USING (SELECT @Feria2Id AS feria2_id) AS source ON target.feria2_id = source.feria2_id
            WHEN MATCHED THEN UPDATE SET
                employee_external_id = @EmployeeExternalId, remote_adjustment_id = @RemoteAdjustmentId,
                payload_hash = @PayloadHash, status = @Status, attempts = @Attempts, last_error = @LastError,
                start_date = @StartDate, end_date = @EndDate, updated_at = @Now
            WHEN NOT MATCHED THEN
                INSERT (feria2_id, employee_external_id, remote_adjustment_id, payload_hash, status, attempts, last_error, start_date, end_date, updated_at)
                VALUES (@Feria2Id, @EmployeeExternalId, @RemoteAdjustmentId, @PayloadHash, @Status, @Attempts, @LastError, @StartDate, @EndDate, @Now);
            """,
            new
            {
                state.Feria2Id, state.EmployeeExternalId, state.RemoteAdjustmentId, state.PayloadHash, state.Status,
                state.Attempts, state.LastError, state.StartDate, state.EndDate, Now = clock.GetUtcNow(),
            },
            cancellationToken: ct));
    }
}

/// <summary>
/// Lock de sessão do sp_getapplock. Liberado explicitamente: fechar a SqlConnection só a devolve
/// ao pool, e a sessão continuaria segurando o lock (as execuções seguintes seriam puladas).
/// </summary>
internal sealed class RunLock(Microsoft.Data.SqlClient.SqlConnection connection, string resource) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "sp_releaseapplock",
                new { Resource = resource, LockOwner = "Session" },
                commandType: CommandType.StoredProcedure));
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
