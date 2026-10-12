using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Worker.Notificacoes;

/// <summary>Configuração, destinatários, estado e histórico dos avisos por e-mail. Tabelas criadas pelo serviço (SqlManagementStore).</summary>
public interface INotificacaoStore
{
    Task<ConfiguracaoNotificacao?> ConfiguracaoAsync(CancellationToken ct);

    /// <summary>Grava a configuração. Com <paramref name="trocarSenha"/> falso, a senha gravada antes é mantida.</summary>
    Task SalvarConfiguracaoAsync(ConfiguracaoNotificacao configuracao, bool trocarSenha, string usuario, CancellationToken ct);

    /// <summary>Registra que o resumo do dia foi processado (enviado ou sem novidade).</summary>
    Task MarcarResumoAsync(DateTimeOffset quando, CancellationToken ct);

    Task<IReadOnlyList<DestinatarioNotificacao>> DestinatariosAsync(CancellationToken ct);

    /// <summary>Inclui (Id = 0) ou altera um destinatário. Devolve o id.</summary>
    Task<long> SalvarDestinatarioAsync(DestinatarioNotificacao destinatario, string usuario, CancellationToken ct);

    Task<EstadoNotificacao?> EstadoAsync(int cdempresa, CancellationToken ct);

    Task SalvarEstadoAsync(EstadoNotificacao estado, CancellationToken ct);

    /// <summary>
    /// Pendências da última execução da empresa: as que continuam ficam abertas, as novas entram (ou reabrem, como
    /// novas) e as que não apareceram mais são resolvidas.
    /// </summary>
    Task AtualizarPendenciasAsync(int cdempresa, IReadOnlyList<PendenciaAtual> atuais, DateTimeOffset agora, CancellationToken ct);

    Task<IReadOnlyList<PendenciaAberta>> PendenciasAbertasAsync(CancellationToken ct);

    Task RegistrarEnvioAsync(EnvioNotificacao envio, CancellationToken ct);

    Task<IReadOnlyList<EnvioNotificacao>> EnviosRecentesAsync(int quantidade, CancellationToken ct);
}

public sealed class SqlNotificacaoStore(ConnectionFactory connections, TimeProvider clock) : INotificacaoStore
{
    private const int TamanhoChave = 400;

    public async Task<ConfiguracaoNotificacao?> ConfiguracaoAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ConfiguracaoNotificacao>(new CommandDefinition("""
            SELECT ativo AS Ativo, smtp_servidor AS Servidor, smtp_porta AS Porta, smtp_usuario AS Usuario, smtp_senha AS SenhaCifrada,
                   remetente AS Remetente, starttls AS StartTls, hora_resumo AS HoraResumo, somente_dias_uteis AS SomenteDiasUteis,
                   lembrete_horas AS LembreteHoras, notificar_simulacao AS NotificarSimulacao, url_web AS UrlWeb,
                   ultimo_resumo AS UltimoResumo, atualizado_por AS AtualizadoPor, atualizado_em AS AtualizadoEm
            FROM solidesdp.notificacao_config WHERE id = 1
            """, cancellationToken: ct));
    }

    public async Task SalvarConfiguracaoAsync(ConfiguracaoNotificacao configuracao, bool trocarSenha, string usuario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuracao);
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.notificacao_config
            SET ativo = @Ativo, smtp_servidor = @Servidor, smtp_porta = @Porta, smtp_usuario = @Usuario,
                smtp_senha = CASE WHEN @TrocarSenha = 1 THEN @SenhaCifrada ELSE smtp_senha END,
                remetente = @Remetente, starttls = @StartTls, hora_resumo = @HoraResumo, somente_dias_uteis = @SomenteDiasUteis,
                lembrete_horas = @LembreteHoras, notificar_simulacao = @NotificarSimulacao, url_web = @UrlWeb,
                atualizado_por = @Usuario2, atualizado_em = @Agora
            WHERE id = 1;

            IF @@ROWCOUNT = 0
                INSERT INTO solidesdp.notificacao_config
                    (id, ativo, smtp_servidor, smtp_porta, smtp_usuario, smtp_senha, remetente, starttls, hora_resumo, somente_dias_uteis,
                     lembrete_horas, notificar_simulacao, url_web, ultimo_resumo, atualizado_por, atualizado_em)
                VALUES (1, @Ativo, @Servidor, @Porta, @Usuario, @SenhaCifrada, @Remetente, @StartTls, @HoraResumo, @SomenteDiasUteis,
                        @LembreteHoras, @NotificarSimulacao, @UrlWeb, NULL, @Usuario2, @Agora);
            """,
            new
            {
                configuracao.Ativo,
                configuracao.Servidor,
                configuracao.Porta,
                configuracao.Usuario,
                configuracao.SenhaCifrada,
                configuracao.Remetente,
                configuracao.StartTls,
                configuracao.HoraResumo,
                configuracao.SomenteDiasUteis,
                configuracao.LembreteHoras,
                configuracao.NotificarSimulacao,
                configuracao.UrlWeb,
                TrocarSenha = trocarSenha,
                Usuario2 = usuario,
                Agora = clock.GetUtcNow(),
            },
            cancellationToken: ct));
    }

    public async Task MarcarResumoAsync(DateTimeOffset quando, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE solidesdp.notificacao_config SET ultimo_resumo = @Quando WHERE id = 1", new { Quando = quando }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DestinatarioNotificacao>> DestinatariosAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<DestinatarioNotificacao>(new CommandDefinition("""
            SELECT id AS Id, email AS Email, cdempresa AS Cdempresa, recebe_falhas AS RecebeFalhas, recebe_resumo AS RecebeResumo, ativo AS Ativo
            FROM solidesdp.notificacao_destinatario
            ORDER BY ativo DESC, email, cdempresa
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<long> SalvarDestinatarioAsync(DestinatarioNotificacao destinatario, string usuario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destinatario);
        await using var connection = await connections.OpenAsync(ct);
        var parametros = new
        {
            destinatario.Id,
            Email = destinatario.Email.Trim(),
            destinatario.Cdempresa,
            destinatario.RecebeFalhas,
            destinatario.RecebeResumo,
            destinatario.Ativo,
            Usuario = usuario,
            Agora = clock.GetUtcNow(),
        };
        if (destinatario.Id > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE solidesdp.notificacao_destinatario
                SET email = @Email, cdempresa = @Cdempresa, recebe_falhas = @RecebeFalhas, recebe_resumo = @RecebeResumo, ativo = @Ativo,
                    atualizado_por = @Usuario, atualizado_em = @Agora
                WHERE id = @Id
                """, parametros, cancellationToken: ct));
            return destinatario.Id;
        }

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO solidesdp.notificacao_destinatario (email, cdempresa, recebe_falhas, recebe_resumo, ativo, criado_por, criado_em)
            OUTPUT INSERTED.id
            VALUES (@Email, @Cdempresa, @RecebeFalhas, @RecebeResumo, @Ativo, @Usuario, @Agora)
            """, parametros, cancellationToken: ct));
    }

    public async Task<EstadoNotificacao?> EstadoAsync(int cdempresa, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<EstadoNotificacao>(new CommandDefinition("""
            SELECT cdempresa AS Cdempresa, em_falha AS EmFalha, falha_desde AS FalhaDesde, ultimo_aviso AS UltimoAviso
            FROM solidesdp.notificacao_estado WHERE cdempresa = @Cdempresa
            """, new { Cdempresa = cdempresa }, cancellationToken: ct));
    }

    public async Task SalvarEstadoAsync(EstadoNotificacao estado, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(estado);
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.notificacao_estado
            SET em_falha = @EmFalha, falha_desde = @FalhaDesde, ultimo_aviso = @UltimoAviso
            WHERE cdempresa = @Cdempresa;

            IF @@ROWCOUNT = 0
                INSERT INTO solidesdp.notificacao_estado (cdempresa, em_falha, falha_desde, ultimo_aviso)
                VALUES (@Cdempresa, @EmFalha, @FalhaDesde, @UltimoAviso);
            """, estado, cancellationToken: ct));
    }

    public async Task AtualizarPendenciasAsync(int cdempresa, IReadOnlyList<PendenciaAtual> atuais, DateTimeOffset agora, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(atuais);
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var linhas = atuais
            .GroupBy(p => Limitar(p.Chave, TamanhoChave), StringComparer.Ordinal)
            .Select(g => new { Cdempresa = cdempresa, Chave = g.Key, Rotulo = Limitar(g.First().Rotulo, 200), g.First().Mensagem, Agora = agora })
            .ToList();
        if (linhas.Count > 0)
        {
            // Reaparecer depois de resolvida conta como nova (primeira_vez = agora).
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE solidesdp.notificacao_pendencia
                SET rotulo = @Rotulo, mensagem = @Mensagem, ultima_vez = @Agora,
                    primeira_vez = CASE WHEN resolvida_em IS NULL THEN primeira_vez ELSE @Agora END,
                    resolvida_em = NULL
                WHERE cdempresa = @Cdempresa AND chave = @Chave;

                IF @@ROWCOUNT = 0
                    INSERT INTO solidesdp.notificacao_pendencia (cdempresa, chave, rotulo, mensagem, primeira_vez, ultima_vez, resolvida_em)
                    VALUES (@Cdempresa, @Chave, @Rotulo, @Mensagem, @Agora, @Agora, NULL);
                """, linhas, transaction, cancellationToken: ct));
        }

        // As que não apareceram nesta execução foram resolvidas.
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE solidesdp.notificacao_pendencia SET resolvida_em = @Agora
            WHERE cdempresa = @Cdempresa AND resolvida_em IS NULL AND ultima_vez < @Agora
            """, new { Cdempresa = cdempresa, Agora = agora }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PendenciaAberta>> PendenciasAbertasAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<PendenciaAberta>(new CommandDefinition("""
            SELECT cdempresa AS Cdempresa, chave AS Chave, rotulo AS Rotulo, mensagem AS Mensagem, primeira_vez AS PrimeiraVez
            FROM solidesdp.notificacao_pendencia
            WHERE resolvida_em IS NULL
            ORDER BY cdempresa, primeira_vez, rotulo
            """, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task RegistrarEnvioAsync(EnvioNotificacao envio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.notificacao_envio (enviado_em, tipo, cdempresa, para, assunto, ok, erro)
            VALUES (@EnviadoEm, @Tipo, @Cdempresa, @Para, @Assunto, @Ok, @Erro)
            """, new { envio.EnviadoEm, envio.Tipo, envio.Cdempresa, envio.Para, Assunto = Limitar(envio.Assunto, 400), envio.Ok, envio.Erro },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EnvioNotificacao>> EnviosRecentesAsync(int quantidade, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<EnvioNotificacao>(new CommandDefinition("""
            SELECT TOP (@Quantidade) id AS Id, enviado_em AS EnviadoEm, tipo AS Tipo, cdempresa AS Cdempresa, para AS Para,
                   assunto AS Assunto, ok AS Ok, erro AS Erro
            FROM solidesdp.notificacao_envio
            ORDER BY id DESC
            """, new { Quantidade = quantidade }, cancellationToken: ct));
        return rows.AsList();
    }

    private static string Limitar(string texto, int tamanho) => texto.Length <= tamanho ? texto : texto[..tamanho];
}
