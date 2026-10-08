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
                         AND OBJECT_ID(N'solidesdp.entity_state', N'U') IS NOT NULL
                         AND OBJECT_ID(N'solidesdp.vacation_state', N'U') IS NOT NULL
                         AND OBJECT_ID(N'solidesdp.comando', N'U') IS NOT NULL
                        THEN 1 ELSE 0 END
            """, cancellationToken: ct));
        _preparada = ok == 1;
        return _preparada;
    }

    // ---------------------------------------------------------------- Execuções

    private const string ExecucaoColunas = """
        run_id AS RunId, status AS Status, dry_run AS DryRun, triggered_by AS Origem, solicitado_por AS SolicitadoPor,
        started_at AS Inicio, finished_at AS Fim, config_versao AS ConfigVersao, summary_json AS ResumoJson, error_message AS Erro
        """;

    private static readonly string[] ExecucoesOrdem =
        ["started_at", "dry_run", "triggered_by", "solicitado_por", "status", "DATEDIFF(SECOND, started_at, finished_at)", "config_versao"];

    public async Task<Execucao?> UltimaExecucaoAsync(bool somenteReal, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<Execucao>(new CommandDefinition($"""
            SELECT TOP (1) {ExecucaoColunas}
            FROM solidesdp.runs
            WHERE instance_name = @Instance AND (@SomenteReal = 0 OR dry_run = 0) AND status <> @Running
            ORDER BY started_at DESC
            """, new { Instance, SomenteReal = somenteReal, Running = RunStatuses.Running }, cancellationToken: ct));
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

    public async Task<DataTablesResponse<Execucao>> ExecucoesAsync(DataTablesRequest request, CancellationToken ct)
    {
        const string Filtro = """
            instance_name = @Instance
            AND (@Busca IS NULL OR status LIKE @Busca ESCAPE '\' OR triggered_by LIKE @Busca ESCAPE '\' OR solicitado_por LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.runs WHERE instance_name = @Instance;
            SELECT COUNT(*) FROM solidesdp.runs WHERE {Filtro};
            SELECT {ExecucaoColunas} FROM solidesdp.runs WHERE {Filtro}
            ORDER BY {request.OrderBy(ExecucoesOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Instance, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
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

    /// <summary>Nome legível de cada cadastro, a partir das tabelas do RHSenso. A chave é a de solidesdp.entity_state.</summary>
    private static string NomeSql(string tipo) => tipo switch
    {
        EntityTypes.Employee => """
            OUTER APPLY (SELECT TOP (1) RTRIM(f.nmcolab) AS Nome FROM dbo.func1 f
                         WHERE f.cdempresa = k.Empresa AND f.nomatric = k.Resto ORDER BY f.dtadmissao DESC) n
            """,
        EntityTypes.Workplace => """
            OUTER APPLY (SELECT TOP (1) COALESCE(RTRIM(t.nmfantasia), RTRIM(t.dcestab)) AS Nome FROM dbo.test1 t
                         WHERE t.cdempresa = k.Empresa AND t.cdfilial = TRY_CAST(k.Resto AS int)) n
            """,
        EntityTypes.JobRole => """
            OUTER APPLY (SELECT TOP (1) RTRIM(c.dccargo) AS Nome FROM dbo.cargo1 c WHERE c.cdcargo = e.external_id) n
            """,
        _ => "OUTER APPLY (SELECT CAST(NULL AS nvarchar(100)) AS Nome) n",
    };

    /// <summary>Separa "{empresa}-{resto}" (colaborador: matrícula; local: filial).</summary>
    private const string ChaveSql = """
        CROSS APPLY (SELECT CHARINDEX('-', e.external_id) AS Traco) p
        CROSS APPLY (SELECT TRY_CAST(LEFT(e.external_id, NULLIF(p.Traco, 0) - 1) AS int) AS Empresa,
                            SUBSTRING(e.external_id, p.Traco + 1, 60) AS Resto) k
        """;

    private static readonly string[] MigradosOrdem = ["e.external_id", "n.Nome", "e.remote_id", "e.status", "e.updated_at"];

    public async Task<DataTablesResponse<RegistroMigrado>> MigradosAsync(string tipo, DataTablesRequest request, CancellationToken ct)
    {
        var from = $"""
            FROM solidesdp.entity_state e
            {ChaveSql}
            {NomeSql(tipo)}
            WHERE e.entity_type = @Tipo
            """;
        const string Busca = "AND (@Busca IS NULL OR e.external_id LIKE @Busca ESCAPE '\\' OR n.Nome LIKE @Busca ESCAPE '\\')";
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.entity_state WHERE entity_type = @Tipo;
            SELECT COUNT(*) {from} {Busca};
            SELECT e.external_id AS ExternalId, n.Nome, e.remote_id AS RemoteId, e.status AS Status, e.updated_at AS AtualizadoEm
            {from} {Busca}
            ORDER BY {request.OrderBy(MigradosOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Tipo = tipo, Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<RegistroMigrado>(grid, request);
    }

    private static readonly string[] FeriasOrdem =
        ["e.employee_external_id", "n.Nome", "f.dtinipf", "f.dtfimpf", "e.remote_adjustment_id", "e.status", "e.attempts", "e.updated_at"];

    public async Task<DataTablesResponse<FeriasMigradas>> FeriasAsync(DataTablesRequest request, CancellationToken ct)
    {
        const string From = """
            FROM solidesdp.vacation_state e
            CROSS APPLY (SELECT CHARINDEX('-', e.employee_external_id) AS Traco) p
            CROSS APPLY (SELECT TRY_CAST(LEFT(e.employee_external_id, NULLIF(p.Traco, 0) - 1) AS int) AS Empresa,
                                SUBSTRING(e.employee_external_id, p.Traco + 1, 60) AS Resto) k
            OUTER APPLY (SELECT TOP (1) RTRIM(fu.nmcolab) AS Nome FROM dbo.func1 fu
                         WHERE fu.cdempresa = k.Empresa AND fu.nomatric = k.Resto ORDER BY fu.dtadmissao DESC) n
            LEFT JOIN dbo.feria2 f ON f.id = e.feria2_id
            WHERE (@Busca IS NULL OR e.employee_external_id LIKE @Busca ESCAPE '\' OR n.Nome LIKE @Busca ESCAPE '\')
            """;
        await using var connection = await connections.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM solidesdp.vacation_state;
            SELECT COUNT(*) {From};
            SELECT e.feria2_id AS Feria2Id, e.employee_external_id AS ExternalId, n.Nome, f.dtinipf AS Inicio, f.dtfimpf AS Fim,
                   e.remote_adjustment_id AS RemoteId, e.status AS Status, e.attempts AS Tentativas, e.last_error AS UltimoErro,
                   e.updated_at AS AtualizadoEm
            {From}
            ORDER BY {request.OrderBy(FeriasOrdem)} OFFSET @Start ROWS FETCH NEXT @Length ROWS ONLY;
            """, new { Busca = request.Like, request.Start, request.Length }, cancellationToken: ct));
        return await LerGradeAsync<FeriasMigradas>(grid, request);
    }

    public async Task<IReadOnlyList<Contagem>> TotaisAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Contagem>(new CommandDefinition("""
            SELECT entity_type AS Tipo, status AS Status, COUNT(*) AS Quantidade
            FROM solidesdp.entity_state WHERE remote_id IS NOT NULL GROUP BY entity_type, status
            UNION ALL
            SELECT 'vacation', status, COUNT(*) FROM solidesdp.vacation_state GROUP BY status
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
        id AS Id, tipo AS Tipo, status AS Status, solicitado_por AS SolicitadoPor, solicitado_em AS SolicitadoEm,
        iniciado_em AS IniciadoEm, concluido_em AS ConcluidoEm, run_id AS RunId
        """;

    private static readonly string[] ComandosOrdem = ["id", "tipo", "status", "solicitado_por", "solicitado_em", "concluido_em"];

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

    public async Task<bool> ComandoEmAbertoAsync(string tipo, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM solidesdp.comando
            WHERE instance_name = @Instance AND tipo = @Tipo AND status IN (@Pendente, @Executando)
            """, new { Instance, Tipo = tipo, Pendente = CommandStatuses.Pending, Executando = CommandStatuses.Running }, cancellationToken: ct)) > 0;
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
