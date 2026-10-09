using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Web.Data;

/// <summary>Empresas e filiais do RHSenso (dbo.temp1 e dbo.test1) e o que cada uma já tem vinculado no Sólides DP.</summary>
public sealed class EmpresasRepository(ConnectionFactory connections)
{
    /// <summary>Empresas ativas na folha (temp1.flativo = 'S'). Só elas podem ser usadas na integração.</summary>
    public async Task<IReadOnlyList<EmpresaRhsenso>> EmpresasAtivasAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EmpresaRhsenso>(new CommandDefinition("""
            SELECT cdempresa AS Cdempresa, RTRIM(nmempresa) AS Nome
            FROM dbo.temp1
            WHERE flativo = 'S'
            ORDER BY cdempresa
            """, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Nome de uma empresa, ativa ou não (para mostrar empresas configuradas que foram inativadas na folha).</summary>
    public async Task<string?> NomeAsync(int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT RTRIM(nmempresa) FROM dbo.temp1 WHERE cdempresa = @Cdempresa",
            new { Cdempresa = cdempresa }, cancellationToken: ct));
    }

    /// <summary>Filiais ativas da empresa (test1.flativofilial = 1).</summary>
    public async Task<IReadOnlyList<FilialRhsenso>> FiliaisAtivasAsync(int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<FilialRhsenso>(new CommandDefinition("""
            SELECT cdfilial AS Cdfilial, COALESCE(RTRIM(dcestab), RTRIM(nmfantasia)) AS Nome, RTRIM(cdcgc) AS Cnpj
            FROM dbo.test1
            WHERE cdempresa = @Cdempresa AND flativofilial = 1
            ORDER BY cdfilial
            """, new { Cdempresa = cdempresa }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Colaboradores vinculados por empresa: criados pela integração x vinculados pelo CPF, e desligados.</summary>
    public async Task<IReadOnlyDictionary<int, VinculosEmpresa>> VinculosAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<VinculosEmpresa>(new CommandDefinition("""
            SELECT cdempresa AS Cdempresa,
                   SUM(CASE WHEN origem = 'criado' AND status <> 'dismissed' THEN 1 ELSE 0 END) AS Criados,
                   SUM(CASE WHEN origem = 'vinculado_cpf' AND status <> 'dismissed' THEN 1 ELSE 0 END) AS VinculadosCpf,
                   SUM(CASE WHEN status = 'dismissed' THEN 1 ELSE 0 END) AS Desligados
            FROM solidesdp.colaborador_vinculo
            WHERE tangerino_id IS NOT NULL
            GROUP BY cdempresa
            """, cancellationToken: ct));
        return rows.ToDictionary(r => r.Cdempresa);
    }
}

public sealed record EmpresaRhsenso
{
    public int Cdempresa { get; init; }
    public string? Nome { get; init; }
}

public sealed record FilialRhsenso
{
    public int Cdfilial { get; init; }
    public string? Nome { get; init; }
    public string? Cnpj { get; init; }
}

public sealed record VinculosEmpresa
{
    public int Cdempresa { get; init; }
    public int Criados { get; init; }
    public int VinculadosCpf { get; init; }
    public int Desligados { get; init; }
}
