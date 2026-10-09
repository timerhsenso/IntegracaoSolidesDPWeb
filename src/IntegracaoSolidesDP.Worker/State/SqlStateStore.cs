using System.Data;
using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.State;

public interface IStateStore
{
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Lock exclusivo por instância e empresa (sp_getapplock). Null se outra execução da empresa estiver em andamento.</summary>
    Task<IAsyncDisposable?> TryAcquireRunLockAsync(string instanceName, int cdempresa, CancellationToken ct);

    /// <summary>Grava o esquema da chave do colaborador na primeira execução e recusa se mudar depois.</summary>
    Task VerifyKeySchemeAsync(string scheme, CancellationToken ct);

    /// <summary>Abre uma execução. <paramref name="cdempresa"/> nulo = execução que não chegou a uma empresa (ex.: configuração inválida).</summary>
    Task<Guid> StartRunAsync(string instanceName, int? cdempresa, bool dryRun, string trigger, CancellationToken ct);

    Task FinishRunAsync(Guid runId, string status, string summaryJson, string? errorMessage, CancellationToken ct);

    /// <summary>
    /// Execuções "running" da empresa que ficaram para trás numa parada abrupta viram "failed".
    /// Só pode ser chamado com o lock da empresa (nenhuma execução viva). Devolve quantas.
    /// </summary>
    Task<int> FailInterruptedRunsAsync(string instanceName, int cdempresa, CancellationToken ct);

    Task AddItemsAsync(Guid runId, IReadOnlyCollection<RunItem> items, CancellationToken ct);

    /// <summary>Vínculos de um tipo (colaborador, cargo, local) na conta da empresa, pela chave da integração.</summary>
    Task<Dictionary<string, EntityState>> LoadEntityStatesAsync(int cdempresa, string entityType, CancellationToken ct);

    Task UpsertEntityStateAsync(int cdempresa, EntityState state, CancellationToken ct);

    Task<Dictionary<Guid, VacationState>> LoadVacationStatesAsync(int cdempresa, CancellationToken ct);

    Task UpsertVacationStateAsync(int cdempresa, VacationState state, CancellationToken ct);
}

/// <summary>
/// Estado da integração no próprio bd_rhu_adn, num schema separado (<c>solidesdp</c>):
/// o login do worker precisa só de leitura em <c>dbo</c> e DDL/DML em <c>solidesdp</c>.
/// Os vínculos são por empresa, porque cada empresa é uma conta do Sólides DP.
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
            error_message nvarchar(max)     NULL,
            cdempresa     int               NULL
        );

        IF COL_LENGTH(N'solidesdp.runs', N'cdempresa') IS NULL
            ALTER TABLE solidesdp.runs ADD cdempresa int NULL;

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

        IF OBJECT_ID(N'solidesdp.colaborador_vinculo', N'U') IS NULL
        CREATE TABLE solidesdp.colaborador_vinculo (
            cdempresa       int               NOT NULL,
            cpf             char(11)          NOT NULL,
            tangerino_id    bigint            NULL,
            codigo_externo  nvarchar(128)     NULL,
            matricula       varchar(16)       NOT NULL,
            cdfilial        int               NOT NULL,
            origem          varchar(16)       NOT NULL,
            payload_hash    char(64)          NULL,
            status          varchar(32)       NOT NULL,
            escala_json     nvarchar(max)     NULL,
            atualizado_em   datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_solidesdp_colaborador_vinculo PRIMARY KEY (cdempresa, cpf)
        );

        IF OBJECT_ID(N'solidesdp.cargo_vinculo', N'U') IS NULL
        CREATE TABLE solidesdp.cargo_vinculo (
            cdempresa       int               NOT NULL,
            cdcargo         varchar(10)       NOT NULL,
            tangerino_id    bigint            NULL,
            payload_hash    char(64)          NULL,
            status          varchar(32)       NOT NULL,
            atualizado_em   datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_solidesdp_cargo_vinculo PRIMARY KEY (cdempresa, cdcargo)
        );

        IF OBJECT_ID(N'solidesdp.local_vinculo', N'U') IS NULL
        CREATE TABLE solidesdp.local_vinculo (
            cdempresa       int               NOT NULL,
            cdfilial        int               NOT NULL,
            tangerino_id    bigint            NULL,
            payload_hash    char(64)          NULL,
            status          varchar(32)       NOT NULL,
            atualizado_em   datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_solidesdp_local_vinculo PRIMARY KEY (cdempresa, cdfilial)
        );

        IF OBJECT_ID(N'solidesdp.ferias_vinculo', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.ferias_vinculo (
                feria2_id        uniqueidentifier  NOT NULL CONSTRAINT pk_solidesdp_ferias_vinculo PRIMARY KEY,
                cdempresa        int               NOT NULL,
                cpf              char(11)          NOT NULL,
                ajuste_id        bigint            NULL,
                payload_hash     char(64)          NULL,
                status           varchar(32)       NOT NULL,
                tentativas       int               NOT NULL CONSTRAINT df_solidesdp_ferias_tentativas DEFAULT 0,
                ultimo_erro      nvarchar(max)     NULL,
                inicio_epoch_ms  bigint            NULL,
                fim_epoch_ms     bigint            NULL,
                atualizado_em    datetimeoffset(7) NOT NULL
            );
            CREATE INDEX ix_solidesdp_ferias_vinculo_empresa ON solidesdp.ferias_vinculo (cdempresa, cpf);
        END;

        -- Tabelas da versão anterior (chave empresa-matrícula). Continuam existindo para a Web até ela
        -- passar a ler as tabelas de vínculo; não são mais gravadas.
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

    /// <summary>MS_Description das tabelas do serviço (idempotente).</summary>
    private static readonly string Descricoes = Management.DescricoesSql.Gerar(
    [
        ("runs", null, "Uma linha por execução da sincronização (por empresa)."),
        ("runs", "cdempresa", "Empresa (conta do Sólides DP) da execução; NULL = execução que parou antes de chegar a uma empresa."),
        ("run_items", null, "Itens de cada execução (os sem alteração ficam só na contagem de runs.summary_json)."),
        ("colaborador_vinculo", null, "Vínculo de cada colaborador (CPF) com o cadastro dele na conta do Sólides DP da empresa."),
        ("colaborador_vinculo", "cpf", "CPF só com dígitos: a identidade do colaborador dentro da conta."),
        ("colaborador_vinculo", "tangerino_id", "Id do colaborador no Sólides DP."),
        ("colaborador_vinculo", "codigo_externo", "Código Externo que está no Sólides DP (usado por outro sistema do cliente)."),
        ("colaborador_vinculo", "matricula", "Matrícula do RHSenso (func1.nomatric)."),
        ("colaborador_vinculo", "cdfilial", "Filial atual no RHSenso (detecta transferência entre filiais)."),
        ("colaborador_vinculo", "origem", "criado = criado pela integração; vinculado_cpf = já existia no Sólides DP e foi vinculado pelo CPF."),
        ("colaborador_vinculo", "payload_hash", "SHA-256 do último envio: sem mudança no RHSenso, nada é reenviado."),
        ("colaborador_vinculo", "escala_json", "Escala e regra de ponto enviadas na criação."),
        ("cargo_vinculo", null, "Cargos do RHSenso (cargo1) criados na conta do Sólides DP da empresa."),
        ("local_vinculo", null, "Filiais do RHSenso (test1) criadas como local de trabalho na conta do Sólides DP da empresa."),
        ("ferias_vinculo", null, "Cada parcela de férias (feria2) e o lançamento de ajuste correspondente no Sólides DP."),
        ("ferias_vinculo", "ajuste_id", "Id do lançamento de ajuste no Sólides DP."),
        ("ferias_vinculo", "tentativas", "Envios que falharam seguidos; no limite, o período só é tentado de novo se mudar."),
        ("ferias_vinculo", "inicio_epoch_ms", "Início enviado (epoch ms), para poder cancelar mesmo se a linha sumir do RHSenso."),
        ("ferias_vinculo", "fim_epoch_ms", "Fim enviado (epoch ms)."),
        ("entity_state", null, "Obsoleta (chave empresa-matrícula da versão 1.0). Não é mais gravada."),
        ("vacation_state", null, "Obsoleta (versão 1.0). Não é mais gravada."),
    ]);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(Descricoes, cancellationToken: ct));
    }

    public async Task<IAsyncDisposable?> TryAcquireRunLockAsync(string instanceName, int cdempresa, CancellationToken ct)
    {
        var resource = FormattableString.Invariant($"solidesdp:{instanceName}:{cdempresa}");
        var connection = await connections.OpenAsync(ct);
        try
        {
            var parameters = new DynamicParameters();
            parameters.Add("@Resource", resource);
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

            return new RunLock(connection, resource);
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
            "SELECT [value] FROM solidesdp.meta WHERE [key] = N'colaborador_chave'", cancellationToken: ct));

        if (current is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO solidesdp.meta ([key], [value]) VALUES (N'colaborador_chave', @Scheme)",
                new { Scheme = scheme }, cancellationToken: ct));
        }
        else if (!string.Equals(current, scheme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"O estado foi gravado com a chave de colaborador '{current}', mas este build usa '{scheme}'. " +
                "Trocar a chave depois do go-live duplicaria colaboradores no Sólides DP.");
        }
    }

    public async Task<Guid> StartRunAsync(string instanceName, int? cdempresa, bool dryRun, string trigger, CancellationToken ct)
    {
        var runId = Guid.CreateVersion7();
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.runs (run_id, instance_name, cdempresa, dry_run, triggered_by, status, started_at)
            VALUES (@RunId, @InstanceName, @Cdempresa, @DryRun, @Trigger, @Status, @Now)
            """,
            new
            {
                RunId = runId, InstanceName = instanceName, Cdempresa = cdempresa, DryRun = dryRun, Trigger = trigger,
                Status = RunStatuses.Running, Now = clock.GetUtcNow(),
            },
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

    public async Task<int> FailInterruptedRunsAsync(string instanceName, int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.runs
            SET status = @Failed, finished_at = @Now,
                error_message = N'interrupted: a execução parou sem terminar (serviço parado ou servidor reiniciado)'
            WHERE instance_name = @InstanceName AND status = @Running AND (cdempresa = @Cdempresa OR cdempresa IS NULL)
            """,
            new { InstanceName = instanceName, Cdempresa = cdempresa, Failed = RunStatuses.Failed, Running = RunStatuses.Running, Now = clock.GetUtcNow() },
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

    public async Task<Dictionary<string, EntityState>> LoadEntityStatesAsync(int cdempresa, string entityType, CancellationToken ct)
    {
        var sql = entityType switch
        {
            EntityTypes.Employee => """
                SELECT @EntityType AS EntityType, cpf AS ExternalId, tangerino_id AS RemoteId, payload_hash AS PayloadHash,
                       status AS Status, escala_json AS ExtraJson, codigo_externo AS CodigoExterno, matricula AS Matricula,
                       cdfilial AS Cdfilial, origem AS Origem, atualizado_em AS UpdatedAt
                FROM solidesdp.colaborador_vinculo
                WHERE cdempresa = @Cdempresa
                """,
            EntityTypes.JobRole => """
                SELECT @EntityType AS EntityType, cdcargo AS ExternalId, tangerino_id AS RemoteId, payload_hash AS PayloadHash,
                       status AS Status, atualizado_em AS UpdatedAt
                FROM solidesdp.cargo_vinculo
                WHERE cdempresa = @Cdempresa
                """,
            EntityTypes.Workplace => """
                SELECT @EntityType AS EntityType, CONCAT(cdempresa, '-', cdfilial) AS ExternalId, tangerino_id AS RemoteId,
                       payload_hash AS PayloadHash, status AS Status, cdfilial AS Cdfilial, atualizado_em AS UpdatedAt
                FROM solidesdp.local_vinculo
                WHERE cdempresa = @Cdempresa
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Tipo sem tabela de vínculo."),
        };

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EntityState>(new CommandDefinition(
            sql, new { Cdempresa = cdempresa, EntityType = entityType }, cancellationToken: ct));
        return rows.ToDictionary(r => r.ExternalId, StringComparer.Ordinal);
    }

    public async Task UpsertEntityStateAsync(int cdempresa, EntityState state, CancellationToken ct)
    {
        var sql = state.EntityType switch
        {
            EntityTypes.Employee => """
                MERGE solidesdp.colaborador_vinculo WITH (HOLDLOCK) AS target
                USING (SELECT @Cdempresa AS cdempresa, @Key AS cpf) AS source
                   ON target.cdempresa = source.cdempresa AND target.cpf = source.cpf
                WHEN MATCHED THEN UPDATE SET
                    tangerino_id = @RemoteId, codigo_externo = @CodigoExterno, matricula = @Matricula, cdfilial = @Cdfilial,
                    origem = @Origem, payload_hash = @PayloadHash, status = @Status, escala_json = @ExtraJson, atualizado_em = @Now
                WHEN NOT MATCHED THEN
                    INSERT (cdempresa, cpf, tangerino_id, codigo_externo, matricula, cdfilial, origem, payload_hash, status, escala_json, atualizado_em)
                    VALUES (@Cdempresa, @Key, @RemoteId, @CodigoExterno, @Matricula, @Cdfilial, @Origem, @PayloadHash, @Status, @ExtraJson, @Now);
                """,
            EntityTypes.JobRole => """
                MERGE solidesdp.cargo_vinculo WITH (HOLDLOCK) AS target
                USING (SELECT @Cdempresa AS cdempresa, @Key AS cdcargo) AS source
                   ON target.cdempresa = source.cdempresa AND target.cdcargo = source.cdcargo
                WHEN MATCHED THEN UPDATE SET
                    tangerino_id = @RemoteId, payload_hash = @PayloadHash, status = @Status, atualizado_em = @Now
                WHEN NOT MATCHED THEN
                    INSERT (cdempresa, cdcargo, tangerino_id, payload_hash, status, atualizado_em)
                    VALUES (@Cdempresa, @Key, @RemoteId, @PayloadHash, @Status, @Now);
                """,
            EntityTypes.Workplace => """
                MERGE solidesdp.local_vinculo WITH (HOLDLOCK) AS target
                USING (SELECT @Cdempresa AS cdempresa, @Cdfilial AS cdfilial) AS source
                   ON target.cdempresa = source.cdempresa AND target.cdfilial = source.cdfilial
                WHEN MATCHED THEN UPDATE SET
                    tangerino_id = @RemoteId, payload_hash = @PayloadHash, status = @Status, atualizado_em = @Now
                WHEN NOT MATCHED THEN
                    INSERT (cdempresa, cdfilial, tangerino_id, payload_hash, status, atualizado_em)
                    VALUES (@Cdempresa, @Cdfilial, @RemoteId, @PayloadHash, @Status, @Now);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.EntityType, "Tipo sem tabela de vínculo."),
        };

        if (state.EntityType is EntityTypes.Employee or EntityTypes.Workplace && state.Cdfilial is null)
        {
            throw new ArgumentException($"Vínculo de {state.EntityType} sem filial.", nameof(state));
        }

        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new
            {
                Cdempresa = cdempresa,
                Key = state.ExternalId,
                state.RemoteId,
                state.PayloadHash,
                state.Status,
                state.ExtraJson,
                state.CodigoExterno,
                Matricula = state.Matricula ?? string.Empty,
                state.Cdfilial,
                Origem = state.Origem ?? OrigensVinculo.Criado,
                Now = clock.GetUtcNow(),
            },
            cancellationToken: ct));
    }

    public async Task<Dictionary<Guid, VacationState>> LoadVacationStatesAsync(int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VacationState>(new CommandDefinition("""
            SELECT feria2_id AS Feria2Id, cpf AS EmployeeExternalId, ajuste_id AS RemoteAdjustmentId,
                   payload_hash AS PayloadHash, status AS Status, tentativas AS Attempts, ultimo_erro AS LastError,
                   inicio_epoch_ms AS StartDate, fim_epoch_ms AS EndDate, atualizado_em AS UpdatedAt
            FROM solidesdp.ferias_vinculo
            WHERE cdempresa = @Cdempresa
            """, new { Cdempresa = cdempresa }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Feria2Id);
    }

    public async Task UpsertVacationStateAsync(int cdempresa, VacationState state, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE solidesdp.ferias_vinculo WITH (HOLDLOCK) AS target
            USING (SELECT @Feria2Id AS feria2_id) AS source ON target.feria2_id = source.feria2_id
            WHEN MATCHED THEN UPDATE SET
                cdempresa = @Cdempresa, cpf = @EmployeeExternalId, ajuste_id = @RemoteAdjustmentId,
                payload_hash = @PayloadHash, status = @Status, tentativas = @Attempts, ultimo_erro = @LastError,
                inicio_epoch_ms = @StartDate, fim_epoch_ms = @EndDate, atualizado_em = @Now
            WHEN NOT MATCHED THEN
                INSERT (feria2_id, cdempresa, cpf, ajuste_id, payload_hash, status, tentativas, ultimo_erro, inicio_epoch_ms, fim_epoch_ms, atualizado_em)
                VALUES (@Feria2Id, @Cdempresa, @EmployeeExternalId, @RemoteAdjustmentId, @PayloadHash, @Status, @Attempts, @LastError, @StartDate, @EndDate, @Now);
            """,
            new
            {
                Cdempresa = cdempresa, state.Feria2Id, state.EmployeeExternalId, state.RemoteAdjustmentId, state.PayloadHash, state.Status,
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
