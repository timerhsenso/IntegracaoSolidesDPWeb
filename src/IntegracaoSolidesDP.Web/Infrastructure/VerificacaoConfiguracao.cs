using System.Reflection;
using System.Security.Cryptography;
using Dapper;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Source;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>
/// <c>IntegracaoSolidesDP.Web.exe --check-config</c>: confere, sem iniciar o site, o que a Web precisa para subir
/// no IIS (connection string, permissões no banco, tabelas do login, chave dos tokens e fuso). Não grava nada no
/// banco. O deploy/windows/install-web.ps1 roda esta verificação e só inicia o site se ela passar.
/// Cada linha sai com [OK], [AVISO] (a Web sobe, mas falta algo) ou [ERRO] (a Web não vai funcionar).
/// </summary>
public static class VerificacaoConfiguracao
{
    public static string Versao() =>
        typeof(VerificacaoConfiguracao).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "desconhecida";

    /// <returns><c>true</c> se não houver nenhum [ERRO].</returns>
    public static async Task<bool> ExecutarAsync(IServiceProvider services, TextWriter output, CancellationToken ct)
    {
        var relatorio = new Relatorio(output);
        await relatorio.OkAsync($"IntegracaoSolidesDP.Web {Versao()}");

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var configuration = sp.GetRequiredService<IConfiguration>();
        var options = sp.GetRequiredService<IOptions<WebOptions>>().Value;

        await VerificarFusoAsync(relatorio, options);

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Rhu")))
        {
            await relatorio.ErroAsync("ConnectionStrings:Rhu está vazia: preencha o appsettings.json da pasta do site.");
            return false;
        }

        SqlConnection connection;
        try
        {
            connection = await sp.GetRequiredService<ConnectionFactory>().OpenAsync(ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            await relatorio.ErroAsync($"Não foi possível conectar no banco: {ex.Message}");
            return false;
        }

        await using (connection)
        {
            var login = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT SUSER_SNAME();", cancellationToken: ct));
            await relatorio.OkAsync($"Banco: {connection.Database} em {connection.DataSource} (login {login})");
            if (new SqlConnectionStringBuilder(connection.ConnectionString).IntegratedSecurity)
            {
                await relatorio.AvisoAsync(
                    $"Autenticação integrada: esta verificação usou a conta {login}. No IIS a conexão usa a identidade do app pool, que precisa das mesmas permissões.");
            }

            var permissoes = await connection.QuerySingleAsync<Permissoes>(new CommandDefinition(Permissoes.Sql, cancellationToken: ct));
            if (!await VerificarRhSensoAsync(relatorio, permissoes))
            {
                return false;
            }

            await VerificarTabelasDoServicoAsync(relatorio, permissoes, sp, ct);
            await VerificarLoginAsync(relatorio, permissoes, options, sp.GetRequiredService<AuthDbContext>(), ct);
        }

        await VerificarChaveTokensAsync(relatorio, options, sp, ct);
        return relatorio.SemErros;
    }

    private static async Task VerificarFusoAsync(Relatorio relatorio, WebOptions options)
    {
        bool existe;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
            existe = true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            existe = false;
        }

        if (existe)
        {
            await relatorio.OkAsync($"Fuso: {options.TimeZone}");
        }
        else
        {
            await relatorio.ErroAsync($"Web:TimeZone \"{options.TimeZone}\" não existe neste servidor (ex.: America/Bahia).");
        }
    }

    private static async Task<bool> VerificarRhSensoAsync(Relatorio relatorio, Permissoes p)
    {
        if (!p.TemFunc1)
        {
            await relatorio.ErroAsync("A tabela dbo.func1 não existe: a connection string aponta para o banco do RHSenso (bd_rhu_adn)?");
            return false;
        }

        if (!p.LerRhSenso)
        {
            await relatorio.ErroAsync("O login não lê as tabelas do RHSenso (dbo): falta db_datareader. Veja deploy/windows/permissoes-sql.sql.");
            return false;
        }

        await relatorio.OkAsync("Leitura das tabelas do RHSenso (dbo)");
        return true;
    }

    private static async Task VerificarTabelasDoServicoAsync(Relatorio relatorio, Permissoes p, IServiceProvider sp, CancellationToken ct)
    {
        if (!p.TemSchemaServico)
        {
            await relatorio.AvisoAsync(
                "O schema solidesdp ainda não existe: rode o serviço com Gestao:Habilitada=true (ou IntegracaoSolidesDP.exe --check-config). A Web sobe e o Painel explica o que falta.");
            return;
        }

        var faltam = new List<string>();
        if (!p.ServicoSelect)
        {
            faltam.Add("SELECT");
        }

        if (!p.ServicoInsert)
        {
            faltam.Add("INSERT");
        }

        if (!p.ServicoUpdate)
        {
            faltam.Add("UPDATE");
        }

        if (faltam.Count > 0)
        {
            await relatorio.ErroAsync(
                $"Falta permissão de {string.Join(", ", faltam)} no schema solidesdp (configuração, pedidos e auditoria). Veja deploy/windows/permissoes-sql.sql.");
            return;
        }

        await relatorio.OkAsync("Leitura e gravação no schema solidesdp");

        if (await sp.GetRequiredService<PainelRepository>().GestaoPreparadaAsync(ct))
        {
            await relatorio.OkAsync("Tabelas da gestão criadas pelo serviço");
        }
        else
        {
            await relatorio.AvisoAsync(
                "As tabelas da gestão ainda não existem ou são de uma versão anterior: atualize o serviço e rode-o com Gestao:Habilitada=true. A Web sobe e o Painel explica o que falta.");
        }
    }

    private static async Task VerificarLoginAsync(Relatorio relatorio, Permissoes p, WebOptions options, AuthDbContext auth, CancellationToken ct)
    {
        List<string> pendentes;
        try
        {
            pendentes = [.. await auth.Database.GetPendingMigrationsAsync(ct)];
        }
        catch (SqlException ex)
        {
            await relatorio.ErroAsync($"Não foi possível ler as tabelas do login (schema {AuthDbContext.Schema}): {ex.Message}");
            return;
        }

        if (p.TemSchemaLogin && !(p.LoginSelect && p.LoginInsert && p.LoginUpdate && p.LoginDelete))
        {
            await relatorio.ErroAsync(
                $"O login precisa ler e gravar (SELECT, INSERT, UPDATE, DELETE) no schema {AuthDbContext.Schema}. Veja deploy/windows/permissoes-sql.sql.");
            return;
        }

        if (pendentes.Count == 0)
        {
            await relatorio.OkAsync($"Tabelas do login ({AuthDbContext.Schema}) em dia");
            return;
        }

        if (!options.AplicarMigrationsNoStart)
        {
            await relatorio.ErroAsync(
                $"{pendentes.Count} migration(s) do login pendente(s) e Web:AplicarMigrationsNoStart=false: o DBA precisa aplicar o script " +
                "(dotnet ef migrations script --idempotent --project src/IntegracaoSolidesDP.Web).");
            return;
        }

        var podeCriar = p.CriarTabela && (p.TemSchemaLogin ? p.LoginAlter : p.CriarSchema);
        if (!podeCriar)
        {
            await relatorio.ErroAsync(
                $"{pendentes.Count} migration(s) do login pendente(s), mas o login não pode criar as tabelas no schema {AuthDbContext.Schema} " +
                "(falta CREATE TABLE e ser dono do schema). Veja deploy/windows/permissoes-sql.sql.");
            return;
        }

        await relatorio.OkAsync($"{pendentes.Count} migration(s) do login serão aplicadas quando o site iniciar");
    }

    private static async Task VerificarChaveTokensAsync(Relatorio relatorio, WebOptions options, IServiceProvider sp, CancellationToken ct)
    {
        var protector = sp.GetRequiredService<ITokenProtector>();
        try
        {
            var teste = protector.Unprotect(protector.Protect("verificacao"));
            if (teste != "verificacao")
            {
                throw new CryptographicException("O texto decifrado não confere.");
            }
        }
        catch (CryptographicException ex)
        {
            await relatorio.ErroAsync($"Chave dos tokens (Web:ChaveTokens): {ex.Message}");
            return;
        }

        await relatorio.OkAsync(string.IsNullOrWhiteSpace(options.ChaveTokens)
            ? "Chave dos tokens: proteção da máquina (DPAPI). Web e serviço precisam estar neste mesmo servidor."
            : "Chave dos tokens: AES-256 (Web:ChaveTokens). Precisa ser igual a Gestao:ChaveTokens do serviço.");

        IReadOnlyDictionary<int, EmpresaToken> tokens;
        try
        {
            tokens = await sp.GetRequiredService<IManagementStore>().GetEmpresaTokensAsync(ct);
        }
        catch (SqlException)
        {
            // Sem a tabela (serviço ainda não criou a gestão): já avisado acima.
            return;
        }

        foreach (var (cdempresa, token) in tokens.OrderBy(t => t.Key))
        {
            if (token.TokenCifrado is null)
            {
                continue;
            }

            try
            {
                protector.Unprotect(token.TokenCifrado);
                await relatorio.OkAsync($"Empresa {cdempresa}: token cadastrado é lido com esta chave");
            }
            catch (CryptographicException)
            {
                await relatorio.ErroAsync(
                    $"Empresa {cdempresa}: o token foi cifrado com outra chave ou em outra máquina. Confira Web:ChaveTokens (igual a Gestao:ChaveTokens) ou cadastre o token de novo na tela Empresas.");
            }
        }
    }

    private sealed class Relatorio(TextWriter output)
    {
        public bool SemErros { get; private set; } = true;

        public Task OkAsync(string mensagem) => output.WriteLineAsync($"[OK] {mensagem}");

        public Task AvisoAsync(string mensagem) => output.WriteLineAsync($"[AVISO] {mensagem}");

        public Task ErroAsync(string mensagem)
        {
            SemErros = false;
            return output.WriteLineAsync($"[ERRO] {mensagem}");
        }
    }

    /// <summary>Permissões efetivas do login (inclui papéis como db_datareader e dono do schema).</summary>
    internal sealed class Permissoes
    {
        public const string Sql = """
            SELECT
                TemFunc1         = CAST(CASE WHEN OBJECT_ID(N'dbo.func1', N'U') IS NOT NULL THEN 1 ELSE 0 END AS bit),
                LerRhSenso       = CAST(ISNULL(HAS_PERMS_BY_NAME(N'dbo.func1', N'OBJECT', N'SELECT'), 0) AS bit),
                TemSchemaServico = CAST(CASE WHEN SCHEMA_ID(N'solidesdp') IS NOT NULL THEN 1 ELSE 0 END AS bit),
                ServicoSelect    = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp', N'SCHEMA', N'SELECT'), 0) AS bit),
                ServicoInsert    = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp', N'SCHEMA', N'INSERT'), 0) AS bit),
                ServicoUpdate    = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp', N'SCHEMA', N'UPDATE'), 0) AS bit),
                TemSchemaLogin   = CAST(CASE WHEN SCHEMA_ID(N'solidesdp_auth') IS NOT NULL THEN 1 ELSE 0 END AS bit),
                LoginSelect      = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp_auth', N'SCHEMA', N'SELECT'), 0) AS bit),
                LoginInsert      = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp_auth', N'SCHEMA', N'INSERT'), 0) AS bit),
                LoginUpdate      = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp_auth', N'SCHEMA', N'UPDATE'), 0) AS bit),
                LoginDelete      = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp_auth', N'SCHEMA', N'DELETE'), 0) AS bit),
                LoginAlter       = CAST(ISNULL(HAS_PERMS_BY_NAME(N'solidesdp_auth', N'SCHEMA', N'ALTER'), 0) AS bit),
                CriarTabela      = CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0) AS bit),
                CriarSchema      = CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE SCHEMA'), 0) AS bit);
            """;

        public bool TemFunc1 { get; set; }

        public bool LerRhSenso { get; set; }

        public bool TemSchemaServico { get; set; }

        public bool ServicoSelect { get; set; }

        public bool ServicoInsert { get; set; }

        public bool ServicoUpdate { get; set; }

        public bool TemSchemaLogin { get; set; }

        public bool LoginSelect { get; set; }

        public bool LoginInsert { get; set; }

        public bool LoginUpdate { get; set; }

        public bool LoginDelete { get; set; }

        public bool LoginAlter { get; set; }

        public bool CriarTabela { get; set; }

        public bool CriarSchema { get; set; }
    }
}
