using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// Erros da aplicação (solidesdp.erro_aplicacao) e a configuração deles (solidesdp.erro_config), tabelas criadas pelo
/// SqlManagementStore. Sem as tabelas (gestão desligada ou serviço ainda não instalado), nada é gravado e valem os padrões.
/// </summary>
public interface IErroStore
{
    /// <summary>
    /// Grava uma ocorrência: o mesmo erro (mesma assinatura) soma na linha que já existe; um erro resolvido que acontece
    /// de novo volta a ficar aberto. Devolve o id (o código E-id), ou null quando a tabela ainda não existe.
    /// </summary>
    Task<long?> RegistrarAsync(ErroCapturado erro, CancellationToken ct);

    /// <summary>Os erros mais recentes (pela última ocorrência), sem o detalhe. Filtros nulos = todos.</summary>
    Task<IReadOnlyList<ErroAplicacao>> ListarAsync(string? situacao, string? origem, int quantidade, CancellationToken ct);

    /// <summary>Um erro com o detalhe; null se não existe.</summary>
    Task<ErroAplicacao?> ObterAsync(long id, CancellationToken ct);

    /// <summary>Silencia, resolve ou reabre. False se o erro não existe ou já estava nessa situação.</summary>
    Task<bool> MudarSituacaoAsync(long id, string situacao, string usuario, CancellationToken ct);

    /// <summary>Quantos erros estão abertos (para o menu). 0 sem a tabela.</summary>
    Task<int> AbertosAsync(CancellationToken ct);

    /// <summary>Apaga os erros cuja última ocorrência é anterior ao limite. Devolve quantos.</summary>
    Task<int> ApagarAntigosAsync(DateTimeOffset limite, CancellationToken ct);

    /// <summary>A configuração gravada, ou os padrões quando não há (sem tabela ou sem linha).</summary>
    Task<ConfiguracaoErros> ConfiguracaoAsync(CancellationToken ct);

    Task SalvarConfiguracaoAsync(ConfiguracaoErros configuracao, string usuario, CancellationToken ct);
}

public sealed class SqlErroStore(ConnectionFactory connections, TimeProvider clock) : IErroStore
{
    /// <summary>Tamanho máximo da mensagem gravada.</summary>
    public const int TamanhoMensagem = 2000;

    /// <summary>Tamanho máximo do detalhe (pilha) gravado.</summary>
    public const int TamanhoDetalhe = 20000;

    private const string Colunas = """
        id AS Id, origem AS Origem, fonte AS Fonte, tipo AS Tipo, mensagem AS Mensagem, usuario AS Usuario, tela AS Tela,
        cdempresa AS Cdempresa, maquina AS Maquina, versao AS Versao, primeira_vez AS PrimeiraVez, ultima_vez AS UltimaVez,
        ocorrencias AS Ocorrencias, situacao AS Situacao, situacao_por AS SituacaoPor, situacao_em AS SituacaoEm,
        execucao_caida AS ExecucaoCaida
        """;

    public async Task<long?> RegistrarAsync(ErroCapturado erro, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(erro);
        var mensagem = ErroTexto.Limpar(erro.Mensagem, TamanhoMensagem);
        if (string.IsNullOrWhiteSpace(mensagem))
        {
            mensagem = erro.Tipo ?? "(erro sem mensagem)";
        }

        var fonte = Cortar(erro.Fonte, 256);
        var tipo = Cortar(erro.Tipo, 256);
        var quando = erro.Quando == default ? clock.GetUtcNow() : erro.Quando;

        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            DECLARE @id bigint = NULL;
            IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NOT NULL
            BEGIN
                BEGIN TRANSACTION;
                SELECT @id = id FROM solidesdp.erro_aplicacao WITH (UPDLOCK, HOLDLOCK) WHERE assinatura = @Assinatura;
                IF @id IS NULL
                BEGIN
                    INSERT INTO solidesdp.erro_aplicacao
                        (assinatura, origem, fonte, tipo, mensagem, detalhe, usuario, tela, cdempresa, maquina, versao,
                         primeira_vez, ultima_vez, ocorrencias, situacao, execucao_caida)
                    VALUES (@Assinatura, @Origem, @Fonte, @Tipo, @Mensagem, @Detalhe, @Usuario, @Tela, @Cdempresa, @Maquina, @Versao,
                            @Quando, @Quando, 1, @Aberto, @ExecucaoCaida);
                    SET @id = CAST(SCOPE_IDENTITY() AS bigint);
                END
                ELSE
                    UPDATE solidesdp.erro_aplicacao
                    SET ultima_vez = CASE WHEN @Quando > ultima_vez THEN @Quando ELSE ultima_vez END,
                        ocorrencias = CASE WHEN ocorrencias < 2147483647 THEN ocorrencias + 1 ELSE ocorrencias END,
                        mensagem = @Mensagem, detalhe = @Detalhe, usuario = @Usuario, tela = @Tela, cdempresa = @Cdempresa,
                        maquina = @Maquina, versao = @Versao,
                        ultimo_aviso = CASE WHEN situacao = @Resolvido THEN NULL ELSE ultimo_aviso END,
                        ocorrencias_no_aviso = CASE WHEN situacao = @Resolvido THEN 0 ELSE ocorrencias_no_aviso END,
                        situacao_por = CASE WHEN situacao = @Resolvido THEN NULL ELSE situacao_por END,
                        situacao_em = CASE WHEN situacao = @Resolvido THEN NULL ELSE situacao_em END,
                        situacao = CASE WHEN situacao = @Resolvido THEN @Aberto ELSE situacao END
                    WHERE id = @id;
                COMMIT TRANSACTION;
            END;
            SELECT @id;
            """,
            new
            {
                Assinatura = ErroTexto.Assinatura(erro.Origem, fonte, tipo, mensagem),
                Origem = Cortar(erro.Origem, 16),
                Fonte = fonte,
                Tipo = tipo,
                Mensagem = mensagem,
                Detalhe = ErroTexto.Limpar(erro.Detalhe, TamanhoDetalhe),
                Usuario = Cortar(erro.Usuario, 64),
                Tela = Cortar(ErroTexto.Limpar(erro.Tela, 400), 400),
                erro.Cdempresa,
                Maquina = Cortar(erro.Maquina, 128),
                Versao = Cortar(erro.Versao, 32),
                Quando = quando,
                erro.ExecucaoCaida,
                Aberto = SituacoesErro.Aberto,
                Resolvido = SituacoesErro.Resolvido,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ErroAplicacao>> ListarAsync(string? situacao, string? origem, int quantidade, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var linhas = await connection.QueryAsync<ErroAplicacao>(new CommandDefinition($"""
            IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NOT NULL
                SELECT TOP (@Quantidade) {Colunas} FROM solidesdp.erro_aplicacao
                WHERE (@Situacao IS NULL OR situacao = @Situacao) AND (@Origem IS NULL OR origem = @Origem)
                ORDER BY ultima_vez DESC, id DESC;
            ELSE
                SELECT CAST(0 AS bigint) AS Id WHERE 1 = 0;
            """,
            new { Situacao = situacao, Origem = origem, Quantidade = Math.Clamp(quantidade, 1, 1000) },
            cancellationToken: ct));
        return linhas.AsList();
    }

    public async Task<ErroAplicacao?> ObterAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ErroAplicacao>(new CommandDefinition($"""
            IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NOT NULL
                SELECT {Colunas}, detalhe AS Detalhe FROM solidesdp.erro_aplicacao WHERE id = @Id;
            ELSE
                SELECT CAST(0 AS bigint) AS Id WHERE 1 = 0;
            """, new { Id = id }, cancellationToken: ct));
    }

    public async Task<bool> MudarSituacaoAsync(long id, string situacao, string usuario, CancellationToken ct)
    {
        if (situacao is not (SituacoesErro.Aberto or SituacoesErro.Silenciado or SituacoesErro.Resolvido))
        {
            throw new ArgumentOutOfRangeException(nameof(situacao), situacao, "Situação de erro desconhecida.");
        }

        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.erro_aplicacao
            SET situacao = @Situacao, situacao_por = @Usuario, situacao_em = @Agora
            WHERE id = @Id AND situacao <> @Situacao
            """,
            new { Id = id, Situacao = situacao, Usuario = Cortar(usuario, 64), Agora = clock.GetUtcNow() },
            cancellationToken: ct)) == 1;
    }

    public async Task<int> AbertosAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NOT NULL
                SELECT COUNT(*) FROM solidesdp.erro_aplicacao WHERE situacao = @Aberto;
            ELSE
                SELECT 0;
            """, new { Aberto = SituacoesErro.Aberto }, cancellationToken: ct));
    }

    public async Task<int> ApagarAntigosAsync(DateTimeOffset limite, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SET NOCOUNT ON;
            DECLARE @apagados int = 0;
            IF OBJECT_ID(N'solidesdp.erro_aplicacao', N'U') IS NOT NULL
            BEGIN
                DELETE FROM solidesdp.erro_aplicacao WHERE ultima_vez < @Limite;
                SET @apagados = @@ROWCOUNT;
            END;
            SELECT @apagados;
            """, new { Limite = limite }, cancellationToken: ct));
    }

    public async Task<ConfiguracaoErros> ConfiguracaoAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var gravada = await connection.QuerySingleOrDefaultAsync<ConfiguracaoErros>(new CommandDefinition("""
            IF OBJECT_ID(N'solidesdp.erro_config', N'U') IS NOT NULL
                SELECT modo_envio AS ModoEnvio, origem_web AS OrigemWeb, origem_servico AS OrigemServico, execucao_caida AS ExecucaoCaida,
                       resumo_horas AS ResumoHoras, limite_dia AS LimiteDia, operador_ve AS OperadorVe, operador_envia AS OperadorEnvia,
                       retencao_dias AS RetencaoDias, nome_cliente AS NomeCliente, atualizado_por AS AtualizadoPor, atualizado_em AS AtualizadoEm
                FROM solidesdp.erro_config WHERE id = 1;
            ELSE
                SELECT CAST(NULL AS varchar(16)) AS ModoEnvio WHERE 1 = 0;
            """, cancellationToken: ct));
        return gravada ?? new ConfiguracaoErros();
    }

    public async Task SalvarConfiguracaoAsync(ConfiguracaoErros configuracao, string usuario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        if (configuracao.ModoEnvio is not (ModosEnvioErro.Desligado or ModosEnvioErro.Manual or ModosEnvioErro.Automatico))
        {
            throw new ArgumentOutOfRangeException(nameof(configuracao), configuracao.ModoEnvio, "Modo de envio desconhecido.");
        }

        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.erro_config
            SET modo_envio = @ModoEnvio, origem_web = @OrigemWeb, origem_servico = @OrigemServico, execucao_caida = @ExecucaoCaida,
                resumo_horas = @ResumoHoras, limite_dia = @LimiteDia, operador_ve = @OperadorVe, operador_envia = @OperadorEnvia,
                retencao_dias = @RetencaoDias, nome_cliente = @NomeCliente, atualizado_por = @Usuario, atualizado_em = @Agora
            WHERE id = 1;

            IF @@ROWCOUNT = 0
                INSERT INTO solidesdp.erro_config
                    (id, modo_envio, origem_web, origem_servico, execucao_caida, resumo_horas, limite_dia, operador_ve, operador_envia,
                     retencao_dias, nome_cliente, atualizado_por, atualizado_em)
                VALUES (1, @ModoEnvio, @OrigemWeb, @OrigemServico, @ExecucaoCaida, @ResumoHoras, @LimiteDia, @OperadorVe, @OperadorEnvia,
                        @RetencaoDias, @NomeCliente, @Usuario, @Agora);
            """,
            new
            {
                configuracao.ModoEnvio,
                configuracao.OrigemWeb,
                configuracao.OrigemServico,
                configuracao.ExecucaoCaida,
                configuracao.ResumoHoras,
                configuracao.LimiteDia,
                configuracao.OperadorVe,
                configuracao.OperadorEnvia,
                configuracao.RetencaoDias,
                NomeCliente = string.IsNullOrWhiteSpace(configuracao.NomeCliente) ? null : Cortar(configuracao.NomeCliente.Trim(), 100),
                Usuario = Cortar(usuario, 64),
                Agora = clock.GetUtcNow(),
            },
            cancellationToken: ct));
    }

    private static string? Cortar(string? texto, int tamanho) =>
        texto is null ? null : texto.Length <= tamanho ? texto : texto[..tamanho];
}
