using System.Text.Json;
using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>Um campo de um colaborador que está diferente no Sólides DP e no RHSenso.</summary>
public sealed record DivergenciaCampo
{
    /// <summary>"empresa-matrícula" (ex.: 15-00000001).</summary>
    public required string Rotulo { get; init; }

    public string? Nome { get; init; }

    /// <summary>CPF só com dígitos: a chave do vínculo. Não vai para a tela (lá aparece o mascarado).</summary>
    public required string Cpf { get; init; }

    public string? CpfMascarado { get; init; }

    /// <summary>Id do cadastro no Sólides DP.</summary>
    public long? CadastroDp { get; init; }

    /// <summary>Nome do campo (<see cref="CamposConferencia"/>).</summary>
    public required string Campo { get; init; }

    /// <summary>O que a integração enviaria hoje, a partir do RHSenso.</summary>
    public string? RhSenso { get; init; }

    /// <summary>O que está no Sólides DP.</summary>
    public string? Dp { get; init; }
}

/// <summary>Uma conferência DP x RHSenso de uma empresa.</summary>
public sealed record Conferencia
{
    public long Id { get; init; }
    public required string Instancia { get; init; }
    public int Cdempresa { get; init; }
    public DateTimeOffset IniciadaEm { get; init; }
    public DateTimeOffset ConcluidaEm { get; init; }

    /// <summary><see cref="GatilhosConferencia"/>.</summary>
    public required string Gatilho { get; init; }

    public string? SolicitadoPor { get; init; }

    /// <summary>Colaboradores ativos e vinculados conferidos.</summary>
    public int Conferidos { get; init; }

    /// <summary>Colaboradores com pelo menos uma divergência.</summary>
    public int Pessoas { get; init; }

    /// <summary>Campos divergentes (linhas).</summary>
    public int Divergencias { get; init; }

    /// <summary>Motivo de a conferência não ter sido feita (token ausente, DP fora do ar...).</summary>
    public string? Erro { get; init; }

    public IReadOnlyList<DivergenciaCampo> Linhas { get; init; } = [];
}

public static class GatilhosConferencia
{
    /// <summary>A conferência semanal (Sync:ConferenciaDiaSemana e Sync:ConferenciaHora).</summary>
    public const string Agendada = "agendada";

    /// <summary>Pedido "Conferir campos" na Web.</summary>
    public const string Pedido = "pedido";
}

/// <summary>Os campos comparados, com o nome que aparece na tela.</summary>
public static class CamposConferencia
{
    public const string Cadastro = "Cadastro no DP";
    public const string Situacao = "Situação";
    public const string Nome = "Nome";
    public const string Pis = "PIS";
    public const string Ctps = "CTPS";
    public const string Serie = "Série da CTPS";
    public const string Nascimento = "Nascimento";
    public const string Admissao = "Admissão";
    public const string Email = "E-mail";
    public const string Telefone = "Telefone";
    public const string Sexo = "Sexo";
    public const string Cargo = "Cargo";
    public const string Local = "Local de trabalho";
    public const string Empresa = "Empresa";

    /// <summary>Ativo no Sólides DP sem colaborador no RHSenso (não é um campo: a pessoa inteira). Pode ser ignorado.</summary>
    public const string SoNoDp = "Só no Sólides DP";
}

/// <summary>Conferências DP x RHSenso (solidesdp.conferencia, criada pelo serviço no SqlManagementStore).</summary>
public interface IConferenciaStore
{
    /// <summary>Grava a conferência com as linhas. Devolve o id.</summary>
    Task<long> SalvarAsync(Conferencia conferencia, CancellationToken ct);

    /// <summary>A conferência com as linhas, ou null.</summary>
    Task<Conferencia?> ObterAsync(long id, CancellationToken ct);

    /// <summary>As mais recentes, sem as linhas. Vazio se a tabela ainda não existe.</summary>
    Task<IReadOnlyList<Conferencia>> RecentesAsync(int quantidade, CancellationToken ct);

    /// <summary>Início da última conferência da empresa (com ou sem erro), ou null.</summary>
    Task<DateTimeOffset?> UltimaAsync(string instancia, int cdempresa, CancellationToken ct);

    /// <summary>Apaga as anteriores a <paramref name="limite"/>, menos a última de cada empresa. Devolve quantas.</summary>
    Task<int> ApagarAntigasAsync(DateTimeOffset limite, CancellationToken ct);

    /// <summary>
    /// "Reenviar do RHSenso": apaga o hash do último envio destes colaboradores (CPF) da empresa, e a próxima execução
    /// reenvia o cadastro inteiro. Devolve quantos vínculos foram marcados.
    /// </summary>
    Task<int> MarcarReenvioAsync(int cdempresa, IReadOnlyCollection<string> cpfs, CancellationToken ct);
}

public sealed class SqlConferenciaStore(ConnectionFactory connections) : IConferenciaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Colunas = """
        id AS Id, instance_name AS Instancia, cdempresa AS Cdempresa, iniciada_em AS IniciadaEm, concluida_em AS ConcluidaEm,
        gatilho AS Gatilho, solicitado_por AS SolicitadoPor, conferidos AS Conferidos, pessoas AS Pessoas,
        divergencias AS Divergencias, erro AS Erro
        """;

    public async Task<long> SalvarAsync(Conferencia conferencia, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conferencia);
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO solidesdp.conferencia
                (instance_name, cdempresa, iniciada_em, concluida_em, gatilho, solicitado_por, conferidos, pessoas, divergencias, erro, linhas_json)
            OUTPUT INSERTED.id
            VALUES (@Instancia, @Cdempresa, @IniciadaEm, @ConcluidaEm, @Gatilho, @SolicitadoPor, @Conferidos, @Pessoas, @Divergencias, @Erro, @LinhasJson)
            """,
            new
            {
                conferencia.Instancia, conferencia.Cdempresa, conferencia.IniciadaEm, conferencia.ConcluidaEm, conferencia.Gatilho,
                conferencia.SolicitadoPor, conferencia.Conferidos, conferencia.Pessoas, conferencia.Divergencias, conferencia.Erro,
                LinhasJson = JsonSerializer.Serialize(conferencia.Linhas, Json),
            },
            cancellationToken: ct));
    }

    public async Task<Conferencia?> ObterAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var linha = await connection.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(
            $"SELECT {Colunas}, linhas_json AS LinhasJson FROM solidesdp.conferencia WHERE id = @Id",
            new { Id = id }, cancellationToken: ct));
        return linha?.Converter(lerLinhas: true);
    }

    public async Task<IReadOnlyList<Conferencia>> RecentesAsync(int quantidade, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var linhas = await connection.QueryAsync<Linha>(new CommandDefinition($"""
            IF OBJECT_ID(N'solidesdp.conferencia', N'U') IS NOT NULL
                SELECT TOP (@Quantidade) {Colunas}, CAST(NULL AS nvarchar(max)) AS LinhasJson
                FROM solidesdp.conferencia
                ORDER BY iniciada_em DESC, id DESC;
            ELSE
                SELECT CAST(0 AS bigint) AS Id WHERE 1 = 0;
            """, new { Quantidade = quantidade }, cancellationToken: ct));
        return linhas.Select(l => l.Converter(lerLinhas: false)).ToList();
    }

    public async Task<DateTimeOffset?> UltimaAsync(string instancia, int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT MAX(iniciada_em) FROM solidesdp.conferencia WHERE instance_name = @Instancia AND cdempresa = @Cdempresa",
            new { Instancia = instancia, Cdempresa = cdempresa }, cancellationToken: ct));
    }

    public async Task<int> ApagarAntigasAsync(DateTimeOffset limite, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SET NOCOUNT ON;
            DECLARE @apagadas int = 0;
            IF OBJECT_ID(N'solidesdp.conferencia', N'U') IS NOT NULL
            BEGIN
                WITH ordem AS (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY instance_name, cdempresa ORDER BY iniciada_em DESC, id DESC) AS n
                    FROM solidesdp.conferencia
                )
                DELETE c FROM solidesdp.conferencia c JOIN ordem o ON o.id = c.id
                WHERE c.iniciada_em < @Limite AND o.n > 1;
                SET @apagadas = @@ROWCOUNT;
            END;
            SELECT @apagadas;
            """, new { Limite = limite }, commandTimeout: 300, cancellationToken: ct));
    }

    public async Task<int> MarcarReenvioAsync(int cdempresa, IReadOnlyCollection<string> cpfs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cpfs);
        if (cpfs.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.colaborador_vinculo
            SET payload_hash = NULL
            WHERE cdempresa = @Cdempresa AND cpf IN @Cpfs AND payload_hash IS NOT NULL
            """, new { Cdempresa = cdempresa, Cpfs = cpfs }, cancellationToken: ct));
    }

    /// <summary>Linha lida do banco (as divergências ficam num JSON).</summary>
    private sealed class Linha
    {
        public long Id { get; set; }
        public string Instancia { get; set; } = string.Empty;
        public int Cdempresa { get; set; }
        public DateTimeOffset IniciadaEm { get; set; }
        public DateTimeOffset ConcluidaEm { get; set; }
        public string Gatilho { get; set; } = string.Empty;
        public string? SolicitadoPor { get; set; }
        public int Conferidos { get; set; }
        public int Pessoas { get; set; }
        public int Divergencias { get; set; }
        public string? Erro { get; set; }
        public string? LinhasJson { get; set; }

        public Conferencia Converter(bool lerLinhas) => new()
        {
            Id = Id,
            Instancia = Instancia,
            Cdempresa = Cdempresa,
            IniciadaEm = IniciadaEm,
            ConcluidaEm = ConcluidaEm,
            Gatilho = Gatilho,
            SolicitadoPor = SolicitadoPor,
            Conferidos = Conferidos,
            Pessoas = Pessoas,
            Divergencias = Divergencias,
            Erro = Erro,
            Linhas = lerLinhas && !string.IsNullOrEmpty(LinhasJson)
                ? JsonSerializer.Deserialize<List<DivergenciaCampo>>(LinhasJson, Json) ?? []
                : [],
        };
    }
}
