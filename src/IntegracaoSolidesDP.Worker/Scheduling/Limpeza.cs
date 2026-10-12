using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Scheduling;

/// <summary>Quantidades apagadas numa limpeza.</summary>
public sealed record ResultadoLimpeza(int Execucoes, int Envios, int Sinais, int Arquivos);

/// <summary>
/// Limpeza diária do histórico, conforme as regras gerais em vigor (Sync:RetencaoExecucoesDias,
/// Sync:RetencaoArquivosDias, Sync:LimpezaHora): execuções antigas (com itens e relatórios), e-mails enviados,
/// sinais de serviços que não rodam mais e arquivos da pasta de relatórios. Vínculos, auditoria e pedidos não são apagados.
/// Nunca lança: um erro só vai para o log e a limpeza é tentada de novo no dia seguinte.
/// </summary>
public sealed class Limpeza(
    ILimpezaStore store,
    ISinalServicoStore sinais,
    IManagementStore management,
    IOptions<SyncOptions> configured,
    IOptions<ManagementOptions> gestao,
    EpochDates dates,
    TimeProvider clock,
    ILogger<Limpeza> logger)
{
    /// <summary>Execuções apagadas por comando (lotes pequenos não seguram o banco).</summary>
    public const int Lote = 200;

    private DateOnly? _ultimoDia;

    /// <summary>Roda a limpeza se já passou da hora configurada e ela ainda não rodou hoje.</summary>
    public async Task<ResultadoLimpeza?> LimparSeDevidoAsync(CancellationToken ct)
    {
        try
        {
            var regras = await RegrasAsync(ct);
            var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), dates.Zone);
            var hoje = DateOnly.FromDateTime(local.DateTime);
            if (_ultimoDia == hoje || local.Hour < regras.LimpezaHora)
            {
                return null;
            }

            // Marca antes: se der erro, não fica tentando a cada ciclo; amanhã tenta de novo.
            _ultimoDia = hoje;
            return await LimparAsync(regras, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na limpeza diária do histórico");
            return null;
        }
    }

    /// <summary>Limpa agora, com as regras informadas.</summary>
    public async Task<ResultadoLimpeza> LimparAsync(SyncOptions regras, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(regras);
        var agora = clock.GetUtcNow();
        var execucoes = 0;
        var envios = 0;
        var removidos = 0;
        var arquivos = 0;

        if (regras.RetencaoExecucoesDias > 0)
        {
            var limite = agora.AddDays(-regras.RetencaoExecucoesDias);
            int apagadas;
            do
            {
                apagadas = await store.ApagarExecucoesAsync(limite, Lote, ct);
                execucoes += apagadas;
            }
            while (apagadas == Lote);

            envios = await store.ApagarEnviosAsync(limite, ct);
            if (gestao.Value.Habilitada)
            {
                removidos = await sinais.ApagarAntigosAsync(limite, ct);
            }
        }

        if (regras.RetencaoArquivosDias > 0)
        {
            arquivos = ApagarArquivos(agora.AddDays(-regras.RetencaoArquivosDias).UtcDateTime);
        }

        var resultado = new ResultadoLimpeza(execucoes, envios, removidos, arquivos);
        logger.LogInformation(
            "Limpeza do histórico: {Execucoes} execuções, {Envios} e-mails, {Sinais} sinais de serviço e {Arquivos} arquivos de relatório apagados " +
            "(retenção de {Dias} dias no banco e {DiasArquivos} dias nos arquivos; 0 = não apaga)",
            execucoes, envios, removidos, arquivos, regras.RetencaoExecucoesDias, regras.RetencaoArquivosDias);
        return resultado;
    }

    /// <summary>Regras gerais em vigor: a versão vigente da configuração com a gestão ligada, senão o appsettings.json.</summary>
    private async Task<SyncOptions> RegrasAsync(CancellationToken ct)
    {
        var baseline = configured.Value;
        if (!gestao.Value.Habilitada)
        {
            return baseline;
        }

        var atual = await management.GetCurrentConfigurationAsync(baseline.InstanceName, ct);
        return atual is null ? baseline : SyncOptionsJson.Deserialize(atual.SyncJson);
    }

    private int ApagarArquivos(DateTime limiteUtc)
    {
        var pasta = configured.Value.ReportDirectory;
        var diretorio = Path.IsPathRooted(pasta) ? pasta : Path.Combine(AppContext.BaseDirectory, pasta);
        if (!Directory.Exists(diretorio))
        {
            return 0;
        }

        var apagados = 0;
        foreach (var arquivo in Directory.EnumerateFiles(diretorio))
        {
            var extensao = Path.GetExtension(arquivo);
            if (!extensao.Equals(".csv", StringComparison.OrdinalIgnoreCase) && !extensao.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(arquivo) < limiteUtc)
                {
                    File.Delete(arquivo);
                    apagados++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Não foi possível apagar o relatório antigo {Arquivo}", arquivo);
            }
        }

        return apagados;
    }
}
