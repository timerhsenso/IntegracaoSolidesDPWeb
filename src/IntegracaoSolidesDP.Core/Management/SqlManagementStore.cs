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

    /// <summary>
    /// Acrescenta uma versão (nunca altera as anteriores) com as mesmas empresas da versão anterior.
    /// Devolve o número da nova versão.
    /// </summary>
    Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, string? note, string createdBy, CancellationToken ct);

    /// <summary>Acrescenta uma versão com estas empresas (substituem as da versão anterior). Devolve o número da nova versão.</summary>
    Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, IReadOnlyList<EmpresaConfiguracao> empresas, string? note, string createdBy,
        CancellationToken ct);

    /// <summary>Grava um token novo para a empresa (só INSERT). <paramref name="tokenCifrado"/> nulo remove o token.</summary>
    Task AddEmpresaTokenAsync(int cdempresa, byte[]? tokenCifrado, string createdBy, CancellationToken ct);

    /// <summary>Token vigente (linha mais recente) de cada empresa que já teve token.</summary>
    Task<IReadOnlyDictionary<int, EmpresaToken>> GetEmpresaTokensAsync(CancellationToken ct);

    Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, CancellationToken ct);

    /// <summary>Pedido para uma empresa (<paramref name="cdempresa"/> nulo = todas as habilitadas).</summary>
    Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, int? cdempresa, CancellationToken ct);

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

        IF OBJECT_ID(N'solidesdp.configuracao_empresa', N'U') IS NULL
        CREATE TABLE solidesdp.configuracao_empresa (
            configuracao_id   int           NOT NULL,
            cdempresa         int           NOT NULL,
            habilitada        bit           NOT NULL,
            dry_run           bit           NOT NULL,
            go_live           date          NULL,
            escala_externa    nvarchar(64)  NULL,
            regra_externa     nvarchar(64)  NULL,
            motivo_ferias_id  bigint        NULL,
            modo_empresa      varchar(16)   NULL,
            CONSTRAINT pk_solidesdp_configuracao_empresa PRIMARY KEY (configuracao_id, cdempresa),
            CONSTRAINT fk_solidesdp_configuracao_empresa_versao FOREIGN KEY (configuracao_id) REFERENCES solidesdp.configuracao (id)
        );

        IF OBJECT_ID(N'solidesdp.configuracao_filial', N'U') IS NULL
        CREATE TABLE solidesdp.configuracao_filial (
            configuracao_id   int  NOT NULL,
            cdempresa         int  NOT NULL,
            cdfilial          int  NOT NULL,
            CONSTRAINT pk_solidesdp_configuracao_filial PRIMARY KEY (configuracao_id, cdempresa, cdfilial),
            CONSTRAINT fk_solidesdp_configuracao_filial_empresa FOREIGN KEY (configuracao_id, cdempresa)
                REFERENCES solidesdp.configuracao_empresa (configuracao_id, cdempresa)
        );

        IF OBJECT_ID(N'solidesdp.empresa_token', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.empresa_token (
                id              int IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_empresa_token PRIMARY KEY,
                cdempresa       int               NOT NULL,
                token_cifrado   varbinary(max)    NULL,
                criado_por      nvarchar(64)      NOT NULL,
                criado_em       datetimeoffset(7) NOT NULL
            );
            CREATE INDEX ix_solidesdp_empresa_token_empresa ON solidesdp.empresa_token (cdempresa, id DESC);
        END;

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

        IF OBJECT_ID(N'solidesdp.runs', N'U') IS NOT NULL AND COL_LENGTH(N'solidesdp.runs', N'cdempresa') IS NULL
            ALTER TABLE solidesdp.runs ADD cdempresa int NULL;

        IF COL_LENGTH(N'solidesdp.comando', N'cdempresa') IS NULL
            ALTER TABLE solidesdp.comando ADD cdempresa int NULL;
        """;

    private const int UniqueKeyViolation = 2627;
    private const int DuplicateKeyRow = 2601;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(SchemaSql, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(Descricoes, cancellationToken: ct));
    }

    public async Task<ConfigurationVersion?> GetCurrentConfigurationAsync(string instanceName, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var version = await connection.QuerySingleOrDefaultAsync<ConfigurationVersion>(new CommandDefinition("""
            SELECT TOP (1) id AS Id, versao AS Version, ativo AS Active, sync_json AS SyncJson, observacao AS Note,
                   criado_por AS CreatedBy, criado_em AS CreatedAt
            FROM solidesdp.configuracao
            WHERE instance_name = @InstanceName
            ORDER BY versao DESC
            """, new { InstanceName = instanceName }, cancellationToken: ct));
        return version is null ? null : version with { Empresas = await LoadEmpresasAsync(connection, version.Id, ct) };
    }

    private static async Task<IReadOnlyList<EmpresaConfiguracao>> LoadEmpresasAsync(SqlConnection connection, int configuracaoId, CancellationToken ct)
    {
        var empresas = await connection.QueryAsync<EmpresaRow>(new CommandDefinition("""
            SELECT cdempresa AS Cdempresa, habilitada AS Habilitada, dry_run AS DryRun, go_live AS GoLive,
                   escala_externa AS Escala, regra_externa AS Regra, motivo_ferias_id AS MotivoFerias, modo_empresa AS ModoEmpresa
            FROM solidesdp.configuracao_empresa
            WHERE configuracao_id = @Id
            ORDER BY cdempresa
            """, new { Id = configuracaoId }, cancellationToken: ct));
        var filiais = (await connection.QueryAsync<(int Cdempresa, int Cdfilial)>(new CommandDefinition("""
            SELECT cdempresa, cdfilial FROM solidesdp.configuracao_filial WHERE configuracao_id = @Id ORDER BY cdempresa, cdfilial
            """, new { Id = configuracaoId }, cancellationToken: ct)))
            .ToLookup(f => f.Cdempresa, f => f.Cdfilial);

        return empresas.Select(e => new EmpresaConfiguracao
        {
            Cdempresa = e.Cdempresa,
            Habilitada = e.Habilitada,
            DryRun = e.DryRun,
            GoLiveDate = e.GoLive is { } d ? DateOnly.FromDateTime(d) : null,
            WorkScheduleExternalId = e.Escala,
            PunchRuleExternalId = e.Regra,
            FeriasMotivoId = e.MotivoFerias,
            ModoEmpresa = e.ModoEmpresa,
            Filiais = filiais[e.Cdempresa].ToList(),
        }).ToList();
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

    public Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, string? note, string createdBy, CancellationToken ct) =>
        AddVersionAsync(instanceName, active, syncJson, empresas: null, note, createdBy, ct);

    public Task<int> AddConfigurationVersionAsync(
        string instanceName, bool active, string syncJson, IReadOnlyList<EmpresaConfiguracao> empresas, string? note, string createdBy,
        CancellationToken ct) =>
        AddVersionAsync(instanceName, active, syncJson, empresas ?? throw new ArgumentNullException(nameof(empresas)), note, createdBy, ct);

    /// <summary>Nova versão; <paramref name="empresas"/> nulo copia as empresas (e filiais) da versão anterior.</summary>
    private async Task<int> AddVersionAsync(
        string instanceName, bool active, string syncJson, IReadOnlyList<EmpresaConfiguracao>? empresas, string? note, string createdBy,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var inserted = await connection.QuerySingleAsync<(int Id, int Versao)>(new CommandDefinition("""
            INSERT INTO solidesdp.configuracao (instance_name, versao, ativo, sync_json, observacao, criado_por, criado_em)
            OUTPUT inserted.id, inserted.versao
            SELECT @InstanceName, ISNULL(MAX(versao), 0) + 1, @Active, @SyncJson, @Note, @CreatedBy, @Now
            FROM solidesdp.configuracao WITH (UPDLOCK, HOLDLOCK)
            WHERE instance_name = @InstanceName;
            """,
            new { InstanceName = instanceName, Active = active, SyncJson = syncJson, Note = note, CreatedBy = createdBy, Now = clock.GetUtcNow() },
            transaction,
            cancellationToken: ct));

        if (empresas is null)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                DECLARE @Anterior int = (
                    SELECT TOP (1) id FROM solidesdp.configuracao
                    WHERE instance_name = @InstanceName AND versao < @Versao
                    ORDER BY versao DESC);

                INSERT INTO solidesdp.configuracao_empresa
                    (configuracao_id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa)
                SELECT @Id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa
                FROM solidesdp.configuracao_empresa
                WHERE configuracao_id = @Anterior;

                INSERT INTO solidesdp.configuracao_filial (configuracao_id, cdempresa, cdfilial)
                SELECT @Id, cdempresa, cdfilial
                FROM solidesdp.configuracao_filial
                WHERE configuracao_id = @Anterior;
                """,
                new { InstanceName = instanceName, inserted.Id, inserted.Versao },
                transaction,
                cancellationToken: ct));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.configuracao_empresa
                    (configuracao_id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa)
                VALUES (@Id, @Cdempresa, @Habilitada, @DryRun, @GoLive, @Escala, @Regra, @MotivoFerias, @ModoEmpresa);
                """,
                empresas.Select(e => new
                {
                    inserted.Id,
                    e.Cdempresa,
                    e.Habilitada,
                    e.DryRun,
                    GoLive = e.GoLiveDate?.ToDateTime(TimeOnly.MinValue),
                    Escala = Vazio(e.WorkScheduleExternalId),
                    Regra = Vazio(e.PunchRuleExternalId),
                    MotivoFerias = e.FeriasMotivoId,
                    ModoEmpresa = Vazio(e.ModoEmpresa),
                }),
                transaction,
                cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.configuracao_filial (configuracao_id, cdempresa, cdfilial) VALUES (@Id, @Cdempresa, @Cdfilial);
                """,
                empresas.SelectMany(e => e.Filiais.Distinct().Select(f => new { inserted.Id, e.Cdempresa, Cdfilial = f })),
                transaction,
                cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return inserted.Versao;
    }

    public async Task AddEmpresaTokenAsync(int cdempresa, byte[]? tokenCifrado, string createdBy, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.empresa_token (cdempresa, token_cifrado, criado_por, criado_em)
            VALUES (@Cdempresa, @Token, @CreatedBy, @Now);
            """,
            new { Cdempresa = cdempresa, Token = tokenCifrado, CreatedBy = createdBy, Now = clock.GetUtcNow() },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<int, EmpresaToken>> GetEmpresaTokensAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EmpresaToken>(new CommandDefinition("""
            SELECT t.cdempresa AS Cdempresa, t.token_cifrado AS TokenCifrado, t.criado_por AS CriadoPor, t.criado_em AS CriadoEm
            FROM solidesdp.empresa_token t
            WHERE t.id = (SELECT MAX(x.id) FROM solidesdp.empresa_token x WHERE x.cdempresa = t.cdempresa)
            """, cancellationToken: ct));
        return rows.ToDictionary(r => r.Cdempresa);
    }

    public Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, CancellationToken ct) =>
        EnqueueCommandAsync(instanceName, type, requestedBy, cdempresa: null, ct);

    public async Task<long> EnqueueCommandAsync(string instanceName, string type, string requestedBy, int? cdempresa, CancellationToken ct)
    {
        if (!CommandTypes.All.Contains(type, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de comando desconhecido.");
        }

        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO solidesdp.comando (instance_name, tipo, status, solicitado_por, solicitado_em, cdempresa)
            OUTPUT inserted.id
            VALUES (@InstanceName, @Type, @Status, @RequestedBy, @Now, @Cdempresa);
            """,
            new
            {
                InstanceName = instanceName, Type = type, Status = CommandStatuses.Pending, RequestedBy = requestedBy, Now = clock.GetUtcNow(),
                Cdempresa = cdempresa,
            },
            cancellationToken: ct));
    }

    public async Task<ClaimedCommand?> ClaimNextCommandAsync(string instanceName, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ClaimedCommand>(new CommandDefinition("""
            WITH proximo AS (
                SELECT TOP (1) status, iniciado_em, id, tipo, solicitado_por, cdempresa
                FROM solidesdp.comando WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE instance_name = @InstanceName AND status = @Pending
                ORDER BY id
            )
            UPDATE proximo SET status = @Running, iniciado_em = @Now
            OUTPUT inserted.id AS Id, inserted.tipo AS Type, inserted.solicitado_por AS RequestedBy, inserted.cdempresa AS Cdempresa;
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

    private static string? Vazio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>Linha de solidesdp.configuracao_empresa (date chega como DateTime pelo Dapper).</summary>
    private sealed record EmpresaRow
    {
        public int Cdempresa { get; init; }
        public bool Habilitada { get; init; }
        public bool DryRun { get; init; }
        public DateTime? GoLive { get; init; }
        public string? Escala { get; init; }
        public string? Regra { get; init; }
        public long? MotivoFerias { get; init; }
        public string? ModoEmpresa { get; init; }
    }

    /// <summary>MS_Description das tabelas da gestão (idempotente).</summary>
    private static readonly string Descricoes = DescricoesSql.Gerar(
    [
        ("configuracao", null, "Versões da configuração da integração (só INSERT). A vigente é a de maior versão da instância."),
        ("configuracao_empresa", null, "Empresas do RHSenso (dbo.temp1) em cada versão da configuração. Cada empresa é uma conta do Sólides DP."),
        ("configuracao_empresa", "configuracao_id", "Versão (solidesdp.configuracao.id)."),
        ("configuracao_empresa", "cdempresa", "Empresa do RHSenso (dbo.temp1.cdempresa)."),
        ("configuracao_empresa", "habilitada", "1 = a integração sincroniza esta empresa."),
        ("configuracao_empresa", "dry_run", "1 = só simula esta empresa (não envia ao Sólides DP)."),
        ("configuracao_empresa", "go_live", "Início do uso do Sólides DP nesta empresa; vazio = data geral da configuração."),
        ("configuracao_empresa", "escala_externa", "externalId da escala dos novos colaboradores; vazio = padrão da conta."),
        ("configuracao_empresa", "regra_externa", "externalId da regra de ponto dos novos colaboradores; vazio = padrão da conta."),
        ("configuracao_empresa", "motivo_ferias_id", "Id do motivo de ajuste FÉRIAS na conta; vazio = descobrir pela descrição."),
        ("configuracao_empresa", "modo_empresa", "Nenhuma (conta com uma só empresa) ou PorCnpj; vazio = regra geral."),
        ("configuracao_filial", null, "Filiais (dbo.test1) que entram na empresa naquela versão. Nenhuma linha = todas as filiais."),
        ("configuracao_filial", "cdfilial", "Filial do RHSenso (dbo.test1.cdfilial)."),
        ("empresa_token", null, "Token do Sólides DP de cada empresa (só INSERT; vale a linha mais recente). Cifrado: a chave fica fora do banco."),
        ("empresa_token", "token_cifrado", "Token cifrado (DPAPI da máquina ou AES-256-GCM com a chave configurada). NULL = token removido."),
        ("empresa_token", "criado_por", "Usuário da Web que gravou o token."),
        ("comando", null, "Fila de pedidos da Web ao serviço e o resultado de cada um."),
        ("comando", "cdempresa", "Empresa do pedido; NULL = todas as empresas habilitadas."),
        ("auditoria", null, "Tudo o que foi feito na Web (só INSERT)."),
    ]);
}
