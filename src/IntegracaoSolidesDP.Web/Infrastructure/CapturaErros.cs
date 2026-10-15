using System.Globalization;
using System.Security.Claims;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Erros;
using Serilog.Core;
using Serilog.Events;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>
/// Sink do Serilog da Web: todo evento de nível Error ou acima vai para a <see cref="FilaErros"/>, com o usuário e a tela
/// da requisição (método e caminho, sem a query string). A exceção não tratada é registrada pela tela de erro, que mostra
/// o código ao usuário. Nunca lança nem espera o banco.
/// </summary>
public sealed class CapturaErrosSink(FilaErros fila, IHttpContextAccessor http) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        var contexto = logEvent.Properties.TryGetValue("SourceContext", out var valor) && valor is ScalarValue { Value: string texto } ? texto : null;
        if (ErroDeLog.Ignorar(contexto))
        {
            return;
        }

        try
        {
            var erro = ErroDeLog.Criar(
                OrigensErro.Web, contexto, logEvent.MessageTemplate.Text, logEvent.RenderMessage(CultureInfo.InvariantCulture),
                logEvent.Exception, Empresa(logEvent), logEvent.Timestamp);
            fila.Enfileirar(http.HttpContext is { } requisicao ? DaRequisicao(erro, requisicao) : erro);
        }
        catch (Exception)
        {
            // Sem registro: o evento continua no arquivo de log.
        }
    }

    /// <summary>Acrescenta o usuário logado e a tela (método e caminho).</summary>
    public static ErroCapturado DaRequisicao(ErroCapturado erro, HttpContext requisicao)
    {
        ArgumentNullException.ThrowIfNull(erro);
        ArgumentNullException.ThrowIfNull(requisicao);
        return erro with
        {
            Usuario = requisicao.User.Identity?.Name,
            Tela = $"{requisicao.Request.Method} {requisicao.Request.Path}",
        };
    }

    private static int? Empresa(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("Empresa", out var valor) && valor is ScalarValue { Value: int numero } ? (int?)numero : null;
}

/// <summary>Grava os erros da fila (a Web tem as tabelas: criadas na partida). Erro do banco vai para o log como aviso.</summary>
public sealed class GravadorErros(FilaErros fila, IErroStore store, TimeProvider clock, ILogger<GravadorErros> logger) : BackgroundService
{
    private static readonly TimeSpan PausaAposFalha = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await fila.AguardarAsync(stoppingToken))
            {
                var resultado = await fila.GravarAsync(store, clock.GetUtcNow(), stoppingToken);
                if (resultado.Falhas > 0)
                {
                    logger.LogWarning(resultado.UltimaFalha,
                        "{Falhas} erro(s) da aplicação não foram gravados no banco (continuam no arquivo de log)", resultado.Falhas);
                    await Task.Delay(PausaAposFalha, clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada do site: o que restou na fila tenta ser gravado no StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limite.CancelAfter(TimeSpan.FromSeconds(5));
            await fila.GravarAsync(store, clock.GetUtcNow(), limite.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível gravar os últimos erros da aplicação na parada do site");
        }
    }
}

/// <summary>
/// Quem vê a tela "Erros da aplicação" (Admin sempre; Operador se a configuração permitir) e quantos erros estão abertos,
/// para o menu. Guardado por 30 segundos: o menu é desenhado em toda tela.
/// </summary>
public sealed class AcessoErros(IErroStore store, TimeProvider clock, ILogger<AcessoErros> logger)
{
    private static readonly TimeSpan Validade = TimeSpan.FromSeconds(30);

    private Situacao? _situacao;

    /// <summary>O usuário pode ver a tela de erros.</summary>
    public async Task<bool> PodeVerAsync(ClaimsPrincipal usuario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (usuario.IsInRole(Perfis.Admin))
        {
            return true;
        }

        return usuario.IsInRole(Perfis.Operador) && (await SituacaoAsync(ct)).OperadorVe;
    }

    /// <summary>Erros abertos (0 se não deu para ler).</summary>
    public async Task<int> AbertosAsync(CancellationToken ct) => (await SituacaoAsync(ct)).Abertos;

    /// <summary>Esquece o que guardou (depois de mudar a configuração ou a situação de um erro).</summary>
    public void Invalidar() => _situacao = null;

    private async Task<Situacao> SituacaoAsync(CancellationToken ct)
    {
        var agora = clock.GetUtcNow();
        if (_situacao is { } guardada && agora - guardada.LidaEm < Validade)
        {
            return guardada;
        }

        Situacao nova;
        try
        {
            var configuracao = await store.ConfiguracaoAsync(ct);
            nova = new Situacao(configuracao.OperadorVe, await store.AbertosAsync(ct), agora);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            logger.LogWarning(ex, "Não foi possível ler os erros da aplicação para o menu");
            nova = new Situacao(OperadorVe: false, Abertos: 0, agora);
        }

        _situacao = nova;
        return nova;
    }

    private sealed record Situacao(bool OperadorVe, int Abertos, DateTimeOffset LidaEm);
}
