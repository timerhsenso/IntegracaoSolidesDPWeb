using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Cadastro ativo no Sólides DP que não está (nem vai estar) no RHSenso e que o RH mandou ignorar na conferência
/// (ex.: coordenadores contratados direto no DP). Por empresa: cada empresa é uma conta do DP.
/// </summary>
public sealed record ColaboradorIgnorado
{
    public long Id { get; init; }
    public int Cdempresa { get; init; }

    /// <summary>CPF só com dígitos, quando o cadastro do DP tem CPF válido.</summary>
    public string? Cpf { get; init; }

    /// <summary>Id do cadastro no Sólides DP.</summary>
    public long CadastroDp { get; init; }

    public string? NomeDp { get; init; }
    public string IgnoradoPor { get; init; } = string.Empty;
    public DateTimeOffset IgnoradoEm { get; init; }

    /// <summary>Quando voltou a ser validado (pela Web ou porque apareceu na folha). Nulo = ignorado.</summary>
    public DateTimeOffset? RemovidoEm { get; init; }

    public string? RemovidoPor { get; init; }
    public string? MotivoRemocao { get; init; }

    public bool Vigente => RemovidoEm is null;

    /// <summary>CPF para a tela (529.***.***-25); null sem CPF.</summary>
    public string? CpfMascarado => Cpf is { Length: 11 } ? string.Concat(Cpf.AsSpan(0, 3), ".***.***-", Cpf.AsSpan(9, 2)) : null;

    /// <summary>O cadastro do DP é este ignorado: pelo CPF, ou pelo id do cadastro quando não há CPF.</summary>
    public bool Corresponde(long cadastroDp, string? cpf) =>
        (Cpf is not null && string.Equals(Cpf, cpf, StringComparison.Ordinal)) || CadastroDp == cadastroDp;
}

/// <summary>Quem marcar como ignorado (vem de uma linha "Só no Sólides DP" da conferência).</summary>
public sealed record NovoIgnorado(string? Cpf, long CadastroDp, string? NomeDp);

/// <summary>Ignorados da conferência (solidesdp.colaborador_ignorado, criada no SqlManagementStore). Nada é apagado.</summary>
public interface IIgnoradoStore
{
    /// <summary>Os ignorados em vigor da empresa. Vazio se a tabela ainda não existe (gestão desligada).</summary>
    Task<IReadOnlyList<ColaboradorIgnorado>> VigentesAsync(int cdempresa, CancellationToken ct);

    /// <summary>Os ignorados em vigor e os que voltaram a ser validados, mais recentes primeiro.</summary>
    Task<IReadOnlyList<ColaboradorIgnorado>> HistoricoAsync(int cdempresa, int quantidade, CancellationToken ct);

    /// <summary>Marca como ignorados; quem já está ignorado (mesmo CPF ou cadastro) fica como está. Devolve quantos entraram.</summary>
    Task<int> IgnorarAsync(int cdempresa, IReadOnlyCollection<NovoIgnorado> pessoas, string usuario, CancellationToken ct);

    /// <summary>Volta a validar (tira da lista, guardando quem, quando e por quê). False se já não estava ignorado.</summary>
    Task<bool> RemoverAsync(int cdempresa, long id, string removidoPor, string motivo, CancellationToken ct);
}

public sealed class SqlIgnoradoStore(ConnectionFactory connections) : IIgnoradoStore
{
    /// <summary>Quem tira da lista automaticamente (a pessoa apareceu na folha).</summary>
    public const string Servico = "serviço";

    private const string Colunas = """
        id AS Id, cdempresa AS Cdempresa, cpf AS Cpf, tangerino_id AS CadastroDp, nome_dp AS NomeDp,
        ignorado_por AS IgnoradoPor, ignorado_em AS IgnoradoEm, removido_em AS RemovidoEm, removido_por AS RemovidoPor,
        motivo_remocao AS MotivoRemocao
        """;

    public async Task<IReadOnlyList<ColaboradorIgnorado>> VigentesAsync(int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var linhas = await connection.QueryAsync<ColaboradorIgnorado>(new CommandDefinition($"""
            IF OBJECT_ID(N'solidesdp.colaborador_ignorado', N'U') IS NOT NULL
                SELECT {Colunas} FROM solidesdp.colaborador_ignorado
                WHERE cdempresa = @Cdempresa AND removido_em IS NULL
                ORDER BY ignorado_em, id;
            ELSE
                SELECT CAST(0 AS bigint) AS Id WHERE 1 = 0;
            """, new { Cdempresa = cdempresa }, cancellationToken: ct));
        return linhas.ToList();
    }

    public async Task<IReadOnlyList<ColaboradorIgnorado>> HistoricoAsync(int cdempresa, int quantidade, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var linhas = await connection.QueryAsync<ColaboradorIgnorado>(new CommandDefinition($"""
            IF OBJECT_ID(N'solidesdp.colaborador_ignorado', N'U') IS NOT NULL
                SELECT TOP (@Quantidade) {Colunas} FROM solidesdp.colaborador_ignorado
                WHERE cdempresa = @Cdempresa
                ORDER BY CASE WHEN removido_em IS NULL THEN 0 ELSE 1 END, COALESCE(removido_em, ignorado_em) DESC, id DESC;
            ELSE
                SELECT CAST(0 AS bigint) AS Id WHERE 1 = 0;
            """, new { Cdempresa = cdempresa, Quantidade = quantidade }, cancellationToken: ct));
        return linhas.ToList();
    }

    public async Task<int> IgnorarAsync(int cdempresa, IReadOnlyCollection<NovoIgnorado> pessoas, string usuario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pessoas);
        if (pessoas.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var incluidos = 0;
        foreach (var pessoa in pessoas)
        {
            incluidos += await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO solidesdp.colaborador_ignorado (cdempresa, cpf, tangerino_id, nome_dp, ignorado_por, ignorado_em)
                SELECT @Cdempresa, @Cpf, @CadastroDp, @NomeDp, @Usuario, SYSDATETIMEOFFSET()
                WHERE NOT EXISTS (
                    SELECT 1 FROM solidesdp.colaborador_ignorado WITH (UPDLOCK, HOLDLOCK)
                    WHERE cdempresa = @Cdempresa AND removido_em IS NULL
                      AND (tangerino_id = @CadastroDp OR (@Cpf IS NOT NULL AND cpf = @Cpf)));
                """,
                new { Cdempresa = cdempresa, pessoa.Cpf, pessoa.CadastroDp, NomeDp = Cortar(pessoa.NomeDp, 200), Usuario = Cortar(usuario, 64) },
                transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return incluidos;
    }

    public async Task<bool> RemoverAsync(int cdempresa, long id, string removidoPor, string motivo, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.colaborador_ignorado
            SET removido_em = SYSDATETIMEOFFSET(), removido_por = @RemovidoPor, motivo_remocao = @Motivo
            WHERE id = @Id AND cdempresa = @Cdempresa AND removido_em IS NULL
            """,
            new { Id = id, Cdempresa = cdempresa, RemovidoPor = Cortar(removidoPor, 64), Motivo = Cortar(motivo, 500) },
            cancellationToken: ct)) == 1;
    }

    private static string? Cortar(string? texto, int tamanho) =>
        texto is null ? null : texto.Length <= tamanho ? texto : texto[..tamanho];
}
