using Dapper;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Web.Data;

/// <summary>Grava em solidesdp.auditoria (só INSERT). Uma falha aqui falha a ação: nada acontece sem registro.</summary>
public sealed class Auditoria(ConnectionFactory connections, TimeProvider clock, IHttpContextAccessor http)
{
    public async Task RegistrarAsync(string acao, string? detalhe = null, string? usuario = null, CancellationToken ct = default)
    {
        var context = http.HttpContext;
        await using var connection = await connections.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO solidesdp.auditoria (usuario, acao, detalhe, ip, ocorrido_em)
            VALUES (@Usuario, @Acao, @Detalhe, @Ip, @Agora)
            """,
            new
            {
                Usuario = Limitar(usuario ?? context?.User.Identity?.Name ?? "anônimo", 64),
                Acao = acao,
                Detalhe = detalhe,
                Ip = Limitar(context?.Connection.RemoteIpAddress?.ToString(), 45),
                Agora = clock.GetUtcNow(),
            },
            cancellationToken: ct));
    }

    private static string? Limitar(string? texto, int tamanho) =>
        texto is null || texto.Length <= tamanho ? texto : texto[..tamanho];
}

/// <summary>Ações gravadas em solidesdp.auditoria.acao.</summary>
public static class AcoesAuditoria
{
    public const string Login = "LOGIN";
    public const string LoginFalhou = "LOGIN_FALHOU";
    public const string LoginBloqueado = "LOGIN_BLOQUEADO";
    public const string LoginRecusado = "LOGIN_USUARIO_DESATIVADO";
    public const string Logout = "LOGOUT";
    public const string SenhaAlterada = "SENHA_ALTERADA";
    public const string PrimeiroAdmin = "PRIMEIRO_ADMIN";
    public const string UsuarioCriado = "USUARIO_CRIADO";
    public const string UsuarioAlterado = "USUARIO_ALTERADO";
    public const string UsuarioSenhaRedefinida = "USUARIO_SENHA_REDEFINIDA";
    public const string UsuarioDesativado = "USUARIO_DESATIVADO";
    public const string UsuarioAtivado = "USUARIO_ATIVADO";
    public const string ConfiguracaoAlterada = "CONFIGURACAO_ALTERADA";
    public const string IntegracaoAtivada = "INTEGRACAO_ATIVADA";
    public const string IntegracaoDesativada = "INTEGRACAO_DESATIVADA";
    public const string ComandoSolicitado = "COMANDO_SOLICITADO";
    public const string EmpresaAlterada = "EMPRESA_ALTERADA";
    public const string EmpresaTokenCadastrado = "EMPRESA_TOKEN_CADASTRADO";
    public const string EmpresaTokenRemovido = "EMPRESA_TOKEN_REMOVIDO";
}
