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

        IF COL_LENGTH(N'solidesdp.configuracao_empresa', N'criar_empresas') IS NULL
            ALTER TABLE solidesdp.configuracao_empresa ADD criar_empresas bit NOT NULL
                CONSTRAINT df_solidesdp_configuracao_empresa_criar DEFAULT 0;

        IF OBJECT_ID(N'solidesdp.configuracao_piloto', N'U') IS NULL
        CREATE TABLE solidesdp.configuracao_piloto (
            configuracao_id   int           NOT NULL,
            cdempresa         int           NOT NULL,
            item              nvarchar(32)  NOT NULL,
            CONSTRAINT pk_solidesdp_configuracao_piloto PRIMARY KEY (configuracao_id, cdempresa, item),
            CONSTRAINT fk_solidesdp_configuracao_piloto_empresa FOREIGN KEY (configuracao_id, cdempresa)
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

        IF OBJECT_ID(N'solidesdp.notificacao_config', N'U') IS NULL
            CREATE TABLE solidesdp.notificacao_config (
                id                  tinyint           NOT NULL CONSTRAINT pk_solidesdp_notificacao_config PRIMARY KEY
                                                      CONSTRAINT ck_solidesdp_notificacao_config_uma CHECK (id = 1),
                ativo               bit               NOT NULL,
                smtp_servidor       nvarchar(200)     NULL,
                smtp_porta          int               NOT NULL,
                smtp_usuario        nvarchar(200)     NULL,
                smtp_senha          varbinary(max)    NULL,
                remetente           nvarchar(200)     NULL,
                starttls            bit               NOT NULL,
                hora_resumo         time(0)           NOT NULL,
                somente_dias_uteis  bit               NOT NULL,
                lembrete_horas      int               NOT NULL,
                notificar_simulacao bit               NOT NULL,
                url_web             nvarchar(400)     NULL,
                ultimo_resumo       datetimeoffset(7) NULL,
                atualizado_por      nvarchar(64)      NOT NULL,
                atualizado_em       datetimeoffset(7) NOT NULL
            );

        IF OBJECT_ID(N'solidesdp.notificacao_destinatario', N'U') IS NULL
            CREATE TABLE solidesdp.notificacao_destinatario (
                id              bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_notificacao_destinatario PRIMARY KEY,
                email           nvarchar(200)     NOT NULL,
                cdempresa       int               NULL,
                recebe_falhas   bit               NOT NULL,
                recebe_resumo   bit               NOT NULL,
                ativo           bit               NOT NULL,
                criado_por      nvarchar(64)      NOT NULL,
                criado_em       datetimeoffset(7) NOT NULL,
                atualizado_por  nvarchar(64)      NULL,
                atualizado_em   datetimeoffset(7) NULL
            );

        IF OBJECT_ID(N'solidesdp.notificacao_estado', N'U') IS NULL
            CREATE TABLE solidesdp.notificacao_estado (
                cdempresa     int               NOT NULL CONSTRAINT pk_solidesdp_notificacao_estado PRIMARY KEY,
                em_falha      bit               NOT NULL,
                falha_desde   datetimeoffset(7) NULL,
                ultimo_aviso  datetimeoffset(7) NULL
            );

        IF OBJECT_ID(N'solidesdp.notificacao_pendencia', N'U') IS NULL
            CREATE TABLE solidesdp.notificacao_pendencia (
                cdempresa     int               NOT NULL,
                chave         nvarchar(400)     NOT NULL,
                rotulo        nvarchar(200)     NOT NULL,
                mensagem      nvarchar(max)     NOT NULL,
                primeira_vez  datetimeoffset(7) NOT NULL,
                ultima_vez    datetimeoffset(7) NOT NULL,
                resolvida_em  datetimeoffset(7) NULL,
                CONSTRAINT pk_solidesdp_notificacao_pendencia PRIMARY KEY (cdempresa, chave)
            );

        IF OBJECT_ID(N'solidesdp.notificacao_envio', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.notificacao_envio (
                id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_notificacao_envio PRIMARY KEY,
                enviado_em  datetimeoffset(7) NOT NULL,
                tipo        varchar(16)       NOT NULL,
                cdempresa   int               NULL,
                para        nvarchar(max)     NOT NULL,
                assunto     nvarchar(400)     NOT NULL,
                ok          bit               NOT NULL,
                erro        nvarchar(max)     NULL
            );
            CREATE INDEX ix_solidesdp_notificacao_envio_data ON solidesdp.notificacao_envio (enviado_em);
        END;

        IF OBJECT_ID(N'solidesdp.servico_sinal', N'U') IS NULL
        CREATE TABLE solidesdp.servico_sinal (
            instance_name nvarchar(64)      NOT NULL,
            maquina       nvarchar(128)     NOT NULL,
            versao        nvarchar(32)      NULL,
            pid           int               NULL,
            iniciado_em   datetimeoffset(7) NOT NULL,
            ultimo_sinal  datetimeoffset(7) NOT NULL,
            CONSTRAINT pk_solidesdp_servico_sinal PRIMARY KEY (instance_name, maquina)
        );

        IF OBJECT_ID(N'solidesdp.conferencia', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.conferencia (
                id             bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_conferencia PRIMARY KEY,
                instance_name  nvarchar(64)      NOT NULL,
                cdempresa      int               NOT NULL,
                iniciada_em    datetimeoffset(7) NOT NULL,
                concluida_em   datetimeoffset(7) NOT NULL,
                gatilho        varchar(16)       NOT NULL,
                solicitado_por nvarchar(64)      NULL,
                conferidos     int               NOT NULL,
                pessoas        int               NOT NULL,
                divergencias   int               NOT NULL,
                erro           nvarchar(max)     NULL,
                linhas_json    nvarchar(max)     NULL
            );
            CREATE INDEX ix_solidesdp_conferencia_empresa ON solidesdp.conferencia (instance_name, cdempresa, iniciada_em DESC);
        END;

        IF OBJECT_ID(N'solidesdp.colaborador_ignorado', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.colaborador_ignorado (
                id             bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_colaborador_ignorado PRIMARY KEY,
                cdempresa      int               NOT NULL,
                cpf            char(11)          NULL,
                tangerino_id   bigint            NOT NULL,
                nome_dp        nvarchar(200)     NULL,
                ignorado_por   nvarchar(64)      NOT NULL,
                ignorado_em    datetimeoffset(7) NOT NULL,
                removido_em    datetimeoffset(7) NULL,
                removido_por   nvarchar(64)      NULL,
                motivo_remocao nvarchar(500)     NULL
            );
            CREATE INDEX ix_solidesdp_colaborador_ignorado_empresa ON solidesdp.colaborador_ignorado (cdempresa, removido_em);
        END;

        IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NULL
        BEGIN
            CREATE TABLE solidesdp.erro_aplicacao (
                id                   bigint IDENTITY(1,1) NOT NULL CONSTRAINT pk_solidesdp_erro_aplicacao PRIMARY KEY,
                assinatura           char(64)          NOT NULL CONSTRAINT uq_solidesdp_erro_aplicacao UNIQUE,
                origem               varchar(16)       NOT NULL,
                fonte                nvarchar(256)     NULL,
                tipo                 nvarchar(256)     NULL,
                mensagem             nvarchar(2000)    NOT NULL,
                detalhe              nvarchar(max)     NULL,
                usuario              nvarchar(64)      NULL,
                tela                 nvarchar(400)     NULL,
                cdempresa            int               NULL,
                maquina              nvarchar(128)     NULL,
                versao               nvarchar(32)      NULL,
                primeira_vez         datetimeoffset(7) NOT NULL,
                ultima_vez           datetimeoffset(7) NOT NULL,
                ocorrencias          int               NOT NULL,
                situacao             varchar(16)       NOT NULL,
                situacao_por         nvarchar(64)      NULL,
                situacao_em          datetimeoffset(7) NULL,
                execucao_caida       bit               NOT NULL,
                ultimo_aviso         datetimeoffset(7) NULL,
                ocorrencias_no_aviso int               NOT NULL CONSTRAINT df_solidesdp_erro_aplicacao_aviso DEFAULT (0)
            );
            CREATE INDEX ix_solidesdp_erro_aplicacao_ultima ON solidesdp.erro_aplicacao (ultima_vez DESC);
        END;

        IF OBJECT_ID(N'solidesdp.erro_config', N'U') IS NULL
        CREATE TABLE solidesdp.erro_config (
            id              int               NOT NULL CONSTRAINT pk_solidesdp_erro_config PRIMARY KEY CONSTRAINT ck_solidesdp_erro_config_id CHECK (id = 1),
            modo_envio      varchar(16)       NOT NULL,
            origem_web      bit               NOT NULL,
            origem_servico  bit               NOT NULL,
            execucao_caida  bit               NOT NULL,
            resumo_horas    int               NOT NULL,
            limite_dia      int               NOT NULL,
            operador_ve     bit               NOT NULL,
            operador_envia  bit               NOT NULL,
            retencao_dias   int               NOT NULL,
            nome_cliente    nvarchar(100)     NULL,
            atualizado_por  nvarchar(64)      NOT NULL,
            atualizado_em   datetimeoffset(7) NOT NULL
        );
        """;

    private const int UniqueKeyViolation = 2627;
    private const int DuplicateKeyRow = 2601;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await CriacaoEsquema.ExecutarAsync(connection, [SchemaSql, Descricoes], ct);
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
                   escala_externa AS Escala, regra_externa AS Regra, motivo_ferias_id AS MotivoFerias, modo_empresa AS ModoEmpresa,
                   criar_empresas AS CriarEmpresas
            FROM solidesdp.configuracao_empresa
            WHERE configuracao_id = @Id
            ORDER BY cdempresa
            """, new { Id = configuracaoId }, cancellationToken: ct));
        var filiais = (await connection.QueryAsync<(int Cdempresa, int Cdfilial)>(new CommandDefinition("""
            SELECT cdempresa, cdfilial FROM solidesdp.configuracao_filial WHERE configuracao_id = @Id ORDER BY cdempresa, cdfilial
            """, new { Id = configuracaoId }, cancellationToken: ct)))
            .ToLookup(f => f.Cdempresa, f => f.Cdfilial);
        var piloto = (await connection.QueryAsync<(int Cdempresa, string Item)>(new CommandDefinition("""
            SELECT cdempresa, item FROM solidesdp.configuracao_piloto WHERE configuracao_id = @Id ORDER BY cdempresa, item
            """, new { Id = configuracaoId }, cancellationToken: ct)))
            .ToLookup(p => p.Cdempresa, p => p.Item);

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
            CriarEmpresasFaltantes = e.CriarEmpresas,
            Filiais = filiais[e.Cdempresa].ToList(),
            Piloto = piloto[e.Cdempresa].ToList(),
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
                    (configuracao_id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa, criar_empresas)
                SELECT @Id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa, criar_empresas
                FROM solidesdp.configuracao_empresa
                WHERE configuracao_id = @Anterior;

                INSERT INTO solidesdp.configuracao_filial (configuracao_id, cdempresa, cdfilial)
                SELECT @Id, cdempresa, cdfilial
                FROM solidesdp.configuracao_filial
                WHERE configuracao_id = @Anterior;

                INSERT INTO solidesdp.configuracao_piloto (configuracao_id, cdempresa, item)
                SELECT @Id, cdempresa, item
                FROM solidesdp.configuracao_piloto
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
                    (configuracao_id, cdempresa, habilitada, dry_run, go_live, escala_externa, regra_externa, motivo_ferias_id, modo_empresa, criar_empresas)
                VALUES (@Id, @Cdempresa, @Habilitada, @DryRun, @GoLive, @Escala, @Regra, @MotivoFerias, @ModoEmpresa, @CriarEmpresas);
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
                    CriarEmpresas = e.CriarEmpresasFaltantes,
                }),
                transaction,
                cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.configuracao_filial (configuracao_id, cdempresa, cdfilial) VALUES (@Id, @Cdempresa, @Cdfilial);
                """,
                empresas.SelectMany(e => e.Filiais.Distinct().Select(f => new { inserted.Id, e.Cdempresa, Cdfilial = f })),
                transaction,
                cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.configuracao_piloto (configuracao_id, cdempresa, item) VALUES (@Id, @Cdempresa, @Item);
                """,
                empresas.SelectMany(e => e.Piloto
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Select(p => new { inserted.Id, e.Cdempresa, Item = p })),
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
        if (!CommandTypes.All.Contains(type, StringComparer.Ordinal) && !string.Equals(type, CommandTypes.TestarEmail, StringComparison.Ordinal))
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
        public bool CriarEmpresas { get; init; }
    }

    /// <summary>MS_Description das tabelas da gestão (idempotente).</summary>
    private static readonly string Descricoes = DescricoesSql.Gerar(
    [
        ("notificacao_config", null, "Avisos por e-mail (uma linha, id = 1): servidor SMTP, resumo diário das pendências e lembretes. Editada na Web (Notificações)."),
        ("notificacao_config", "smtp_senha", "Senha do SMTP cifrada com a mesma chave dos tokens (Gestao:ChaveTokens / Web:ChaveTokens)."),
        ("notificacao_config", "hora_resumo", "Hora local (Execution:TimeZone) do resumo diário das pendências."),
        ("notificacao_config", "lembrete_horas", "Com a falha continuando, um lembrete a cada tantas horas."),
        ("notificacao_config", "ultimo_resumo", "Quando o último resumo diário foi processado (enviado ou sem novidade)."),
        ("notificacao_destinatario", null, "Quem recebe os avisos. cdempresa nulo = todas as empresas."),
        ("notificacao_destinatario", "recebe_falhas", "1 = recebe na hora as falhas e o aviso de que voltou ao normal."),
        ("notificacao_destinatario", "recebe_resumo", "1 = recebe o resumo diário das pendências novas."),
        ("notificacao_estado", null, "Situação de cada empresa para os avisos de falha: o aviso sai só quando muda (ok → falha, falha → ok) e no lembrete."),
        ("notificacao_pendencia", null, "Pendências (itens bloqueados ou ignorados por dado do RHSenso) vistas nas execuções, para o resumo diário."),
        ("notificacao_pendencia", "chave", "entidade|registro|código do motivo: a mesma pendência tem sempre a mesma chave."),
        ("notificacao_pendencia", "primeira_vez", "Quando apareceu (ou reapareceu depois de resolvida): define o que é novo no resumo."),
        ("notificacao_pendencia", "resolvida_em", "Quando deixou de aparecer nas execuções. Nulo = em aberto."),
        ("conferencia", null, "Conferência DP x RHSenso: campos dos colaboradores vinculados que estão diferentes no Sólides DP (semanal ou pedida na Web)."),
        ("conferencia", "gatilho", "agendada = a semanal (Sync:ConferenciaDiaSemana/ConferenciaHora); pedido = Conferir campos na Web."),
        ("conferencia", "pessoas", "Colaboradores com pelo menos um campo diferente."),
        ("conferencia", "linhas_json", "Uma linha por campo diferente: pessoa, campo, valor no RHSenso e no Sólides DP."),
        ("colaborador_ignorado", null, "Cadastros ativos no Sólides DP sem colaborador no RHSenso que o RH mandou ignorar na conferência (por empresa). Nada é apagado: quem volta a ser validado ganha removido_em."),
        ("colaborador_ignorado", "cpf", "CPF só com dígitos do cadastro no DP; nulo quando o DP não tem CPF válido (aí vale o tangerino_id)."),
        ("colaborador_ignorado", "tangerino_id", "Id do cadastro no Sólides DP."),
        ("colaborador_ignorado", "removido_em", "Quando voltou a ser validado: pela Web (Voltar a validar) ou pelo serviço, quando o CPF apareceu ativo na folha da empresa. Nulo = ignorado."),
        ("colaborador_ignorado", "removido_por", "Login de quem voltou a validar, ou serviço (a pessoa apareceu na folha)."),
        ("erro_aplicacao", null, "Erros técnicos da Web e do serviço (exceções e logs de nível Error). O mesmo erro fica numa linha só e conta as ocorrências. Apagados pela limpeza diária depois de erro_config.retencao_dias sem acontecer."),
        ("erro_aplicacao", "id", "O código que o usuário vê na tela de erro e informa ao suporte (E-id)."),
        ("erro_aplicacao", "assinatura", "SHA-256 de origem, fonte, tipo da exceção e mensagem sem números: agrupa as ocorrências do mesmo erro."),
        ("erro_aplicacao", "origem", "web ou servico."),
        ("erro_aplicacao", "mensagem", "Mensagem com CPF mascarado e token/senha apagados."),
        ("erro_aplicacao", "detalhe", "Exceção completa (pilha), também limpa de CPF e segredos."),
        ("erro_aplicacao", "situacao", "aberto, silenciado (continua contando, não avisa) ou resolvido (se acontecer de novo, volta a aberto)."),
        ("erro_aplicacao", "execucao_caida", "1 = execução que caiu (DP ou banco fora), registrada só com erro_config.execucao_caida ligado."),
        ("erro_aplicacao", "ultimo_aviso", "Quando o último e-mail deste erro saiu para o suporte."),
        ("erro_aplicacao", "ocorrencias_no_aviso", "Ocorrências já contadas no último e-mail (o resumo manda só as novas)."),
        ("erro_config", null, "Configuração dos erros da aplicação (uma linha, id = 1). Editada na Web (Notificações). Sem linha, valem os padrões."),
        ("erro_config", "modo_envio", "desligado, manual (só pelo botão na Web) ou automatico."),
        ("erro_config", "resumo_horas", "Erro que continua acontecendo: um resumo a cada tantas horas."),
        ("erro_config", "limite_dia", "Máximo de e-mails de erro por dia (para não encher a caixa do suporte)."),
        ("erro_config", "operador_ve", "1 = o perfil Operador vê a tela Erros da aplicação."),
        ("erro_config", "retencao_dias", "Dias sem acontecer até o erro ser apagado. 0 = nunca apaga."),
        ("servico_sinal", null, "Sinal de vida de cada serviço (instância e máquina): a Web avisa quando o último sinal fica velho (Sync:ServicoSemRespostaMinutos)."),
        ("servico_sinal", "ultimo_sinal", "Último sinal (a cada Gestao:IntervaloSinalDeVida, também durante as execuções)."),
        ("servico_sinal", "iniciado_em", "Quando o processo atual do serviço começou."),
        ("notificacao_envio", null, "E-mails enviados ou tentados (falha, lembrete, normalizou, resumo, teste), com o erro quando não saiu."),
        ("configuracao", null, "Versões da configuração da integração (só INSERT). A vigente é a de maior versão da instância."),
        ("configuracao_empresa", null, "Empresas do RHSenso (dbo.temp1) em cada versão da configuração. Cada empresa é uma conta do Sólides DP."),
        ("configuracao_empresa", "configuracao_id", "Versão (solidesdp.configuracao.id)."),
        ("configuracao_empresa", "cdempresa", "Empresa do RHSenso (dbo.temp1.cdempresa)."),
        ("configuracao_empresa", "habilitada", "1 = a integração sincroniza esta empresa."),
        ("configuracao_empresa", "dry_run", "1 = só simula esta empresa (não envia ao Sólides DP)."),
        ("configuracao_empresa", "go_live", "Início do uso do Sólides DP nesta empresa. Obrigatória para o envio real."),
        ("configuracao_empresa", "escala_externa", "externalId da escala dos novos colaboradores; vazio = padrão da conta."),
        ("configuracao_empresa", "regra_externa", "externalId da regra de ponto dos novos colaboradores; vazio = padrão da conta."),
        ("configuracao_empresa", "motivo_ferias_id", "Id do motivo de ajuste FÉRIAS na conta; vazio = descobrir pela descrição."),
        ("configuracao_empresa", "modo_empresa", "Nenhuma (conta com uma só empresa) ou PorCnpj (padrão)."),
        ("configuracao_empresa", "criar_empresas", "1 = cria no Sólides DP a empresa (CNPJ da filial) que não existir na conta."),
        ("configuracao_piloto", null, "Piloto da empresa naquela versão: só estes colaboradores (CPF, matrícula ou empresa-matrícula). Nenhuma linha = todos."),
        ("configuracao_piloto", "item", "CPF, matrícula ou empresa-matrícula de um colaborador do piloto."),
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
