namespace IntegracaoSolidesDP.Tests.Sql;

/// <summary>Monta linhas do RHSenso no fixture com valores padrão realistas.</summary>
public sealed class RhuSeed(SqlServerFixture db)
{
    public Task CargoAsync(string code = "00100", string description = "ANALISTA DE SISTEMAS", string? cbo = "212405") =>
        db.ExecuteAsync("INSERT INTO dbo.cargo1 (cdcargo, dccargo, cdcbo6) VALUES (@code, @description, @cbo)", new { code, description, cbo });

    /// <summary>Filial (test1) e, se ainda não existir, a empresa (temp1) ativa.</summary>
    public async Task FilialAsync(int empresa = 1, int filial = 1, string nome = "ADN MATRIZ", string cnpj = "00594807000108", bool ativa = true)
    {
        await EmpresaAsync(empresa);
        await db.ExecuteAsync(
            "INSERT INTO dbo.test1 (cdempresa, cdfilial, nmfantasia, dcestab, cdcgc, flativofilial) VALUES (@empresa, @filial, @nome, @nome, @cnpj, @ativa)",
            new { empresa, filial, nome, cnpj, ativa = ativa ? 1 : 0 });
    }

    /// <summary>Empresa em temp1 (idempotente); <paramref name="ativa"/> = flativo 'S'.</summary>
    public Task EmpresaAsync(int empresa = 1, bool ativa = true, string nome = "ADN SERVICOS") =>
        db.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM dbo.temp1 WHERE cdempresa = @empresa)
                INSERT INTO dbo.temp1 (cdempresa, nmempresa, flativo) VALUES (@empresa, @nome, @flativo);
            ELSE
                UPDATE dbo.temp1 SET flativo = @flativo WHERE cdempresa = @empresa;
            """, new { empresa, nome, flativo = ativa ? "S" : "N" });

    public Task CentroCustoAsync(string code = "00080", string description = "Despesas Corporativas") =>
        db.ExecuteAsync("INSERT INTO dbo.tcus1 (cdccusto, dcccusto) VALUES (@code, @description)", new { code, description });

    public async Task<Guid> FuncionarioAsync(
        string matric = "00000001",
        int empresa = 1,
        int filial = 1,
        string situacao = "01",
        int tipo = 1,
        string? cpf = TestData.Cpf1,
        string cargo = "00100",
        string nome = "MARIA DA SILVA",
        DateTime? admissao = null,
        DateTime? demissao = null,
        DateTime? transferencia = null,
        string? causa = null,
        string? email = "maria@adn.com.br")
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT INTO dbo.func1 (id, nomatric, nmcolab, cdempresa, cdfilial, cdccusto, tpcolab, dtdemissao, dtadmissao, dttransf, cdcausres,
                                   nocpf, nopis, nocartprof, noserie, dtnasc, cdsexo, cdestcivil, cdinstruc, cod_raca, dcemail,
                                   emailalternativo, noddd, notelefone, nmmaecolab, nmpaicolab, cdcargo, cdsituacao)
            VALUES (@id, @matric, @nome, @empresa, @filial, '00080', @tipo, @demissao, @admissao, @transferencia, @causa,
                    @cpf, '12345678919', '1234567', '0012', '1990-05-17', 'F', 'S', '09', 8, @email,
                    NULL, '71', '99999-0000', 'ANA DA SILVA', NULL, @cargo, @situacao)
            """,
            new { id, matric, nome, empresa, filial, tipo, demissao, admissao = admissao ?? new DateTime(2020, 3, 2), transferencia, causa, cpf, email, cargo, situacao });
        return id;
    }

    public async Task<Guid> FeriasAsync(string matric, DateTime inicio, DateTime fim, int situacao = 6, int empresa = 1, int filial = 1, Guid? id = null)
    {
        var feriaId = id ?? Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT INTO dbo.feria2 (id, nomatric, cdempresa, cdfilial, dtinipa, dtinipf, dtfimpf, qtdiasfe, qtabono, flconfirm)
            VALUES (@feriaId, @matric, @empresa, @filial, '2025-01-01', @inicio, @fim, DATEDIFF(day, @inicio, @fim) + 1, 0, @situacao)
            """,
            new { feriaId, matric, empresa, filial, inicio, fim, situacao });
        return feriaId;
    }

    /// <summary>Uma empresa com uma filial, cargo e centro de custo — o mínimo para um colaborador sincronizar.</summary>
    public async Task BasicsAsync()
    {
        await CargoAsync();
        await FilialAsync();
        await CentroCustoAsync();
    }
}
