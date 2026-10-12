using Dapper;
using IntegracaoSolidesDP.Worker.Source;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>
/// Versões em uso, mostradas no rodapé do menu: a da Web (este assembly) e a do serviço (gravada pelo serviço em
/// solidesdp.runs.versao_servico a cada execução). A do serviço é relida no máximo uma vez por minuto.
/// </summary>
public sealed class VersoesSistema(ConnectionFactory connections, IOptions<WebOptions> options, TimeProvider clock, ILogger<VersoesSistema> logger)
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(1);

    // A coluna só existe com o serviço numa versão que a grava: sem ela, não há o que mostrar.
    private const string UltimaVersaoSql = """
        IF COL_LENGTH(N'solidesdp.runs', N'versao_servico') IS NOT NULL
            EXEC sp_executesql
                N'SELECT TOP (1) versao_servico FROM solidesdp.runs WHERE instance_name = @Instance AND versao_servico IS NOT NULL ORDER BY started_at DESC',
                N'@Instance nvarchar(64)', @Instance = @Instance;
        """;

    private const string VersaoDaExecucaoSql = """
        IF COL_LENGTH(N'solidesdp.runs', N'versao_servico') IS NOT NULL
            EXEC sp_executesql N'SELECT versao_servico FROM solidesdp.runs WHERE run_id = @RunId',
                N'@RunId uniqueidentifier', @RunId = @RunId;
        """;

    private volatile Leitura? _servico;

    public string Web { get; } = VerificacaoConfiguracao.Versao();

    /// <summary>Versão do serviço na execução mais recente desta instância; null se ainda não há execução com versão.</summary>
    public async Task<string?> ServicoAsync(CancellationToken ct)
    {
        var lida = _servico;
        var agora = clock.GetUtcNow();
        if (lida is not null && agora - lida.Em < Validade)
        {
            return lida.Versao;
        }

        string? versao = null;
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            versao = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                UltimaVersaoSql, new { Instance = options.Value.InstanceName }, cancellationToken: ct));
        }
        catch (SqlException ex)
        {
            logger.LogWarning(ex, "Não foi possível ler a versão do serviço");
        }

        _servico = new Leitura(versao, agora);
        return versao;
    }

    /// <summary>Versão do serviço que fez uma execução; null nas execuções de versões que não gravavam.</summary>
    public async Task<string?> ServicoDaExecucaoAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            VersaoDaExecucaoSql, new { RunId = runId }, cancellationToken: ct));
    }

    private sealed record Leitura(string? Versao, DateTimeOffset Em);
}
