using Dapper;
using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Data;

/// <summary>
/// Consultas da Web (somente leitura) às tabelas do serviço (schema solidesdp) e do RHSenso (dbo).
/// A ordenação vem sempre de listas fechadas de colunas; o texto digitado só entra como parâmetro.
/// </summary>
public sealed class PainelRepository(ConnectionFactory connections, IOptions<WebOptions> options)
{
    private static volatile bool _preparada;

    private string Instance => options.Value.InstanceName;

    /// <summary>As tabelas que a Web lê já existem (o serviço rodou com Gestao:Habilitada).</summary>
    public async Task<bool> GestaoPreparadaAsync(CancellationToken ct)
    {
        if (_preparada)
        {
            return true;
        }

        await using var connection = await connections.OpenAsync(ct);
        var ok = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT CASE WHEN OBJECT_ID(N'solidesdp.runs', N'U') IS NOT NULL
                         AND COL_LENGTH(N'solidesdp.runs', N'solicitado_por') IS NOT NULL
                         AND COL_LENGTH(N'solidesdp.runs', N'cdempresa') IS NOT NULL
                         AND OBJECT_ID(N'solidesdp.colaborador_vinculo', N'U') IS NOT NULL
                         AND OBJECT_ID(N'solidesdp.ferias_vinculo', N'U') IS NOT NULL
                         AND COL_LENGTH(N'solidesdp.comando', N'cdempresa') IS NOT NULL
                         AND OBJECT_ID(N'solidesdp.empresa_token', N'U') IS NOT NULL
                        THEN 1 ELSE 0 END
            """, cancellationToken: ct));
        _preparada = ok == 1;
        return _preparada;
    }

    // ---------------------------------------------------------------- Execuções

    private const string ExecucaoColunas = """
        run_id AS RunId, cdempresa AS Cdempresa, status AS Status, dry_run AS DryRun, triggered_by AS Origem, solicitado_por AS SolicitadoPor,
        started_at AS Inicio, finished_at AS Fim, config_versao AS ConfigVersao, summary_json AS ResumoJson, error_message AS Erro
        """;

    private static readonly string[] ExecucoesOrdem =
        ["started_at", "cdempresa", "dry_run", "triggered_by", "solicitado_por", "status", "DATEDIFF(SECOND, started_at, finished_at)", "config_versao"];

    /// <summary>Última execução terminada (de uma empresa, ou de qualquer uma com <paramref name="cdempresa"/> nulo).</summary>
    public async Task<Execucao?> UltimaExecucaoAsync(bool somenteReal, CancellationToken ct, int? cdempresa = null)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<Execucao>(new CommandDefinition($"""
            SELECT TOP (1) {ExecucaoColunas}
            FROM solidesdp.runs
            WHERE instance_name = @Instance AND (@SomenteReal = 0 OR dry_run = 0) AND status <> @Running
              AND (@Cdempresa IS NULL OR cdempresa = @Cdempresa)
            ORDER BY started_at DESC
            """, new { Instance, SomenteReal = somenteReal, Running = RunStatuses.Running, Cdempresa = cdempresa }, cancellationToken: ct));
    }

    /// <summary>Última execução terminada de cada empresa.</summary>
    public async Task<IReadOnlyList<Execucao>> UltimasPorEmpresaAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Execucao>(new CommandDefinition($"""
            SELECT {ExecucaoColunas}
            FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY cdempresa ORDER BY started_at DESC) AS ordem
                  FROM solidesdp.runs
                  WHERE instance_name = @Instance AND cdempresa IS NOT NULL AND status <> @Running) r
            WHERE r.ordem = 1
            """, new { Instance, Running = RunStatuses.Running }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Execucao?> ExecucaoAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<Execucao>(new CommandDefinition(
            $"SELECT {ExecucaoColunas} FROM solidesdp.runs WHERE run_id = @RunId", new { RunId = runId }, cancellationToken: ct));
    }

    public async Task<Execucao?> ExecucaoEmAndamentoAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<Execucao>(new CommandDefinition($"""
            SELECT TOP (1) {ExecucaoColunas} FROM solidesdp.runs
            WHERE instance_name = @Instance AND status = @Running ORDER BY started_at DESC
            """, new { Instance, Running = RunStatuses.Running }, cancellationToken: ct));
    }

    public async Task<DataTablesResponse<Execucao>> ExecucoesAsync(DataTablesRequest request, CancellationToken ct, int? cdempresa = null)
    {
        const string Filtro = """
            instance_name = @Instance
            AND (@Cdempresa IS NULL OR cdempresa = @Cdempresa)
            AND (@Busca IS NULL OR status LIKE @Busca ESCAPE '\' OR triggered_by LIKE @Busca ESCAPE '\' OR solicitado_por LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.runs WHERE instance_name = @Instance;
            SELECT COUNT(*) FROM solidesdp.runs WHERE {Filtro};
            SELECT {ExecucaoColunas} FROM solidesdp.runs WHERE {Filtro}
            ORDER BY {request.OrderBy(ExecucoesOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Instance, Cdempresa = cdempresa, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<Execucao>(grid, request);
    }

    public async Task<IReadOnlyList<ExecucaoNoDia>> ExecucoesDesdeAsync(DateTimeOffset desde, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<ExecucaoNoDia>(new CommandDefinition("""
            SELECT started_at AS Inicio, status AS Status FROM solidesdp.runs
            WHERE instance_name = @Instance AND started_at >= @Desde
            """, new { Instance, Desde = desde }, cancellationToken: ct));
        return rows.AsList();
    }

    // ---------------------------------------------------------------- Itens de uma execução

    /// <summary>Status que pedem atenção do RH (tela de pendências).</summary>
    public static readonly IReadOnlyList<string> StatusPendencia = ["failed", "blocked", "skipped", "warning", "deferred"];

    private static readonly string[] ItensOrdem = ["id", "entity_type", "external_id", "action", "status", "http_status", "message"];

    public async Task<DataTablesResponse<ItemExecucao>> ItensAsync(
        Guid runId, string? entidade, string? status, bool somentePendencias, DataTablesRequest request, CancellationToken ct)
    {
        const string Filtro = """
            run_id = @RunId
            AND (@Entidade IS NULL OR entity_type = @Entidade)
            AND (@Status IS NULL OR status = @Status)
            AND (@SomentePendencias = 0 OR status IN @StatusPendencia)
            AND (@Busca IS NULL OR external_id LIKE @Busca ESCAPE '\' OR message LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @RunId;
            SELECT COUNT(*) FROM solidesdp.run_items WHERE {Filtro};
            SELECT id AS Id, entity_type AS Entidade, external_id AS ExternalId, action AS Acao, status AS Status,
                   http_status AS Http, message AS Mensagem
            FROM solidesdp.run_items WHERE {Filtro}
            ORDER BY {request.OrderBy(ItensOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """,
            new
            {
                RunId = runId,
                Entidade = Vazio(entidade),
                Status = Vazio(status),
                SomentePendencias = somentePendencias,
                StatusPendencia,
                Busca = request.Like,
                request.Start,
                request.Length,
            },
            cancellationToken: ct));
        return await LerGradeAsync<ItemExecucao>(grid, request);
    }

    public async Task<int> PendenciasAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM solidesdp.run_items WHERE run_id = @RunId AND status IN ('failed', 'blocked')",
            new { RunId = runId }, cancellationToken: ct));
    }

    // ---------------------------------------------------------------- O que foi migrado

    /// <summary>
    /// Vínculos de um tipo com o Sólides DP (tabelas *_vinculo), com o nome tirado do RHSenso. O código mostrado é o
    /// mesmo dos itens das execuções: colaborador "{empresa}-{matrícula}" (nunca o CPF), local "{empresa}-{filial}", cargo o código.
    /// </summary>
    private static string MigradosFrom(string tipo) => tipo switch
    {
        EntityTypes.Employee => """
            FROM solidesdp.colaborador_vinculo v
            CROSS APPLY (SELECT CONCAT(v.cdempresa, '-', RTRIM(v.matricula)) AS Codigo) c
            OUTER APPLY (SELECT TOP (1) RTRIM(f.nmcolab) AS Nome FROM dbo.func1 f
                         WHERE f.cdempresa = v.cdempresa AND RTRIM(f.nomatric) = RTRIM(v.matricula) ORDER BY f.dtadmissao DESC) n
            """,
        EntityTypes.JobRole => """
            FROM solidesdp.cargo_vinculo v
            CROSS APPLY (SELECT RTRIM(v.cdcargo) AS Codigo) c
            OUTER APPLY (SELECT TOP (1) RTRIM(g.dccargo) AS Nome FROM dbo.cargo1 g WHERE RTRIM(g.cdcargo) = RTRIM(v.cdcargo)) n
            """,
        EntityTypes.Workplace => """
            FROM solidesdp.local_vinculo v
            CROSS APPLY (SELECT CONCAT(v.cdempresa, '-', v.cdfilial) AS Codigo) c
            OUTER APPLY (SELECT TOP (1) COALESCE(RTRIM(t.nmfantasia), RTRIM(t.dcestab)) AS Nome FROM dbo.test1 t
                         WHERE t.cdempresa = v.cdempresa AND t.cdfilial = v.cdfilial) n
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo sem tabela de vínculo."),
    };

    private static string MigradosExtras(string tipo) => tipo == EntityTypes.Employee
        ? "v.origem AS Origem, v.codigo_externo AS CodigoExterno"
        : "CAST(NULL AS varchar(16)) AS Origem, CAST(NULL AS nvarchar(128)) AS CodigoExterno";

    private static readonly string[] MigradosOrdem = ["v.cdempresa", "c.Codigo", "n.Nome", "v.tangerino_id", "v.status", "v.atualizado_em"];

    public async Task<DataTablesResponse<RegistroMigrado>> MigradosAsync(string tipo, int? cdempresa, DataTablesRequest request, CancellationToken ct)
    {
        var from = MigradosFrom(tipo);
        const string Filtro = """
            WHERE (@Cdempresa IS NULL OR v.cdempresa = @Cdempresa)
              AND (@Busca IS NULL OR c.Codigo LIKE @Busca ESCAPE '\' OR n.Nome LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) {from} WHERE (@Cdempresa IS NULL OR v.cdempresa = @Cdempresa);
            SELECT COUNT(*) {from} {Filtro};
            SELECT v.cdempresa AS Cdempresa, c.Codigo AS ExternalId, n.Nome, v.tangerino_id AS RemoteId, v.status AS Status,
                   {MigradosExtras(tipo)}, v.atualizado_em AS AtualizadoEm
            {from} {Filtro}
            ORDER BY {request.OrderBy(MigradosOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Cdempresa = cdempresa, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<RegistroMigrado>(grid, request);
    }

    private static readonly string[] FeriasOrdem =
        ["e.cdempresa", "c.Codigo", "n.Nome", "f.dtinipf", "f.dtfimpf", "e.ajuste_id", "e.status", "e.tentativas", "e.atualizado_em"];

    public async Task<DataTablesResponse<FeriasMigradas>> FeriasAsync(int? cdempresa, DataTablesRequest request, CancellationToken ct)
    {
        const string From = """
            FROM solidesdp.ferias_vinculo e
            LEFT JOIN solidesdp.colaborador_vinculo v ON v.cdempresa = e.cdempresa AND v.cpf = e.cpf
            CROSS APPLY (SELECT CONCAT(e.cdempresa, '-', RTRIM(v.matricula)) AS Codigo) c
            OUTER APPLY (SELECT TOP (1) RTRIM(fu.nmcolab) AS Nome FROM dbo.func1 fu
                         WHERE fu.cdempresa = e.cdempresa AND RTRIM(fu.nomatric) = RTRIM(v.matricula) ORDER BY fu.dtadmissao DESC) n
            LEFT JOIN dbo.feria2 f ON f.id = e.feria2_id
            WHERE (@Cdempresa IS NULL OR e.cdempresa = @Cdempresa)
            """;
        const string Busca = "AND (@Busca IS NULL OR c.Codigo LIKE @Busca ESCAPE '\\' OR n.Nome LIKE @Busca ESCAPE '\\')";
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) {From};
            SELECT COUNT(*) {From} {Busca};
            SELECT e.feria2_id AS Feria2Id, e.cdempresa AS Cdempresa, c.Codigo AS ExternalId, n.Nome, f.dtinipf AS Inicio, f.dtfimpf AS Fim,
                   e.ajuste_id AS RemoteId, e.status AS Status, e.tentativas AS Tentativas, e.ultimo_erro AS UltimoErro,
                   e.atualizado_em AS AtualizadoEm
            {From} {Busca}
            ORDER BY {request.OrderBy(FeriasOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Cdempresa = cdempresa, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<FeriasMigradas>(grid, request);
    }

    public async Task<IReadOnlyList<Contagem>> TotaisAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Contagem>(new CommandDefinition("""
            SELECT 'employee' AS Tipo, status AS Status, COUNT(*) AS Quantidade
            FROM solidesdp.colaborador_vinculo WHERE tangerino_id IS NOT NULL GROUP BY status
            UNION ALL
            SELECT 'job_role', status, COUNT(*) FROM solidesdp.cargo_vinculo WHERE tangerino_id IS NOT NULL GROUP BY status
            UNION ALL
            SELECT 'workplace', status, COUNT(*) FROM solidesdp.local_vinculo WHERE tangerino_id IS NOT NULL GROUP BY status
            UNION ALL
            SELECT 'vacation', status, COUNT(*) FROM solidesdp.ferias_vinculo GROUP BY status
            """, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Últimos itens de um cadastro (para colaborador, inclui as férias dele: "{chave}:{feria2.id}").</summary>
    public async Task<IReadOnlyList<HistoricoItem>> HistoricoAsync(string externalId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<HistoricoItem>(new CommandDefinition("""
            SELECT TOP (200) r.started_at AS Data, r.dry_run AS DryRun, i.run_id AS RunId, i.entity_type AS Entidade,
                   i.external_id AS ExternalId, i.action AS Acao, i.status AS Status, i.http_status AS Http, i.message AS Mensagem
            FROM solidesdp.run_items i
            JOIN solidesdp.runs r ON r.run_id = i.run_id
            WHERE i.external_id = @Id OR i.external_id LIKE @Prefixo ESCAPE '\'
            ORDER BY i.id DESC
            """, new { Id = externalId, Prefixo = Sql.Like(externalId + ":")[1..] }, cancellationToken: ct));
        return rows.AsList();
    }

    // ---------------------------------------------------------------- Comandos

    private const string ComandoColunas = """
        id AS Id, cdempresa AS Cdempresa, tipo AS Tipo, status AS Status, solicitado_por AS SolicitadoPor, solicitado_em AS SolicitadoEm,
        iniciado_em AS IniciadoEm, concluido_em AS ConcluidoEm, run_id AS RunId
        """;

    private static readonly string[] ComandosOrdem = ["id", "tipo", "cdempresa", "status", "solicitado_por", "solicitado_em", "concluido_em"];

    public async Task<DataTablesResponse<Comando>> ComandosAsync(DataTablesRequest request, CancellationToken ct)
    {
        const string Filtro = """
            instance_name = @Instance
            AND (@Busca IS NULL OR tipo LIKE @Busca ESCAPE '\' OR status LIKE @Busca ESCAPE '\' OR solicitado_por LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.comando WHERE instance_name = @Instance;
            SELECT COUNT(*) FROM solidesdp.comando WHERE {Filtro};
            SELECT {ComandoColunas} FROM solidesdp.comando WHERE {Filtro}
            ORDER BY {request.OrderBy(ComandosOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Instance, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<Comando>(grid, request);
    }

    public async Task<IReadOnlyList<Comando>> ComandosRecentesAsync(int quantidade, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Comando>(new CommandDefinition(
            $"SELECT TOP (@Quantidade) {ComandoColunas} FROM solidesdp.comando WHERE instance_name = @Instance ORDER BY id DESC",
            new { Instance, Quantidade = quantidade }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<Comando?> ComandoAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<Comando>(new CommandDefinition(
            $"SELECT {ComandoColunas}, resultado AS Resultado FROM solidesdp.comando WHERE id = @Id AND instance_name = @Instance",
            new { Id = id, Instance }, cancellationToken: ct));
    }

    /// <summary>Já há um pedido igual (mesmo tipo e mesma empresa, ou "todas") esperando ou em execução.</summary>
    public async Task<bool> ComandoEmAbertoAsync(string tipo, int? cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM solidesdp.comando
            WHERE instance_name = @Instance AND tipo = @Tipo AND status IN (@Pendente, @Executando)
              AND ((@Cdempresa IS NULL AND cdempresa IS NULL) OR cdempresa = @Cdempresa)
            """,
            new { Instance, Tipo = tipo, Cdempresa = cdempresa, Pendente = CommandStatuses.Pending, Executando = CommandStatuses.Running },
            cancellationToken: ct)) > 0;
    }

    /// <summary>Pedido mais antigo ainda esperando o serviço (indica serviço parado ou com a gestão desligada).</summary>
    public async Task<DateTimeOffset?> PedidoMaisAntigoAguardandoAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT MIN(solicitado_em) FROM solidesdp.comando WHERE instance_name = @Instance AND status = @Pendente",
            new { Instance, Pendente = CommandStatuses.Pending }, cancellationToken: ct));
    }

    // ---------------------------------------------------------------- Configuração

    public async Task<IReadOnlyList<VersaoConfiguracao>> VersoesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VersaoConfiguracao>(new CommandDefinition("""
            SELECT versao AS Versao, ativo AS Ativo, sync_json AS SyncJson, observacao AS Observacao, criado_por AS CriadoPor, criado_em AS CriadoEm
            FROM solidesdp.configuracao WHERE instance_name = @Instance ORDER BY versao DESC
            """, new { Instance }, cancellationToken: ct));
        return rows.AsList();
    }

    // ---------------------------------------------------------------- Auditoria

    private static readonly string[] AuditoriaOrdem = ["id", "usuario", "acao", "detalhe", "ip"];

    public async Task<DataTablesResponse<RegistroAuditoria>> AuditoriaAsync(DataTablesRequest request, CancellationToken ct)
    {
        const string Filtro = "@Busca IS NULL OR usuario LIKE @Busca ESCAPE '\\' OR acao LIKE @Busca ESCAPE '\\' OR detalhe LIKE @Busca ESCAPE '\\'";
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.auditoria;
            SELECT COUNT(*) FROM solidesdp.auditoria WHERE {Filtro};
            SELECT id AS Id, ocorrido_em AS OcorridoEm, usuario AS Usuario, acao AS Acao, detalhe AS Detalhe, ip AS Ip
            FROM solidesdp.auditoria WHERE {Filtro}
            ORDER BY {request.OrderBy(AuditoriaOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<RegistroAuditoria>(grid, request);
    }

    // ----------------------------------------------------------------

    private static async Task<DataTablesResponse<T>> LerGradeAsync<T>(SqlMapper.GridReader grid, DataTablesRequest request)
    {
        var total = await grid.ReadSingleAsync<int>();
        var filtered = await grid.ReadSingleAsync<int>();
        var data = (await grid.ReadAsync<T>()).AsList();
        return new DataTablesResponse<T>(request.Draw, total, filtered, data);
    }

    private static string? Vazio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
