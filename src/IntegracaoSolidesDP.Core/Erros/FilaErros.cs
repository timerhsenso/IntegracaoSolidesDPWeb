using System.Reflection;
using System.Threading.Channels;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// Fila em memória entre o log (o sink do Serilog, que não pode esperar o banco) e a gravação em solidesdp.erro_aplicacao.
/// Limitada: numa enxurrada de erros os excedentes são descartados (continuam no arquivo de log).
/// </summary>
public sealed class FilaErros
{
    /// <summary>Capacidade da fila.</summary>
    public const int Capacidade = 1000;

    private static readonly TimeSpan ValidadeConfiguracao = TimeSpan.FromMinutes(1);

    private readonly Channel<ErroCapturado> _fila;
    private long _descartados;
    private ConfiguracaoErros? _configuracao;
    private DateTimeOffset _configuracaoLidaEm;

    public FilaErros()
    {
        _fila = Channel.CreateBounded<ErroCapturado>(
            new BoundedChannelOptions(Capacidade) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            _ => Interlocked.Increment(ref _descartados));
    }

    /// <summary>Erros que não couberam na fila desde a partida.</summary>
    public long Descartados => Interlocked.Read(ref _descartados);

    /// <summary>Põe na fila sem esperar. Com a fila cheia o erro é descartado (fica só no arquivo de log) e conta em <see cref="Descartados"/>.</summary>
    public void Enfileirar(ErroCapturado erro)
    {
        ArgumentNullException.ThrowIfNull(erro);
        _ = _fila.Writer.TryWrite(erro);
    }

    /// <summary>Espera até haver erro na fila. False quando a fila foi encerrada.</summary>
    public ValueTask<bool> AguardarAsync(CancellationToken ct) => _fila.Reader.WaitToReadAsync(ct);

    /// <summary>Encerra a fila (parada da aplicação).</summary>
    public void Encerrar() => _fila.Writer.TryComplete();

    /// <summary>
    /// Grava tudo o que está na fila. Execução que caiu só é gravada com a opção ligada (configuração lida no máximo a cada
    /// minuto). Uma falha do banco não interrompe: o erro que não foi gravado é descartado e conta em <c>falhas</c>.
    /// </summary>
    /// <param name="store">Onde gravar.</param>
    /// <param name="agora">Hora atual (validade da configuração em memória).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<ResultadoGravacaoErros> GravarAsync(IErroStore store, DateTimeOffset agora, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var gravados = 0;
        var ignorados = 0;
        var falhas = 0;
        Exception? ultimaFalha = null;
        while (_fila.Reader.TryRead(out var erro))
        {
            try
            {
                if (erro.ExecucaoCaida && !(await ConfiguracaoAsync(store, agora, ct)).ExecucaoCaida)
                {
                    ignorados++;
                    continue;
                }

                if (await store.RegistrarAsync(erro, ct) is null)
                {
                    ignorados++;
                }
                else
                {
                    gravados++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                falhas++;
                ultimaFalha = ex;
            }
        }

        return new ResultadoGravacaoErros(gravados, ignorados, falhas, ultimaFalha);
    }

    private async Task<ConfiguracaoErros> ConfiguracaoAsync(IErroStore store, DateTimeOffset agora, CancellationToken ct)
    {
        if (_configuracao is null || agora - _configuracaoLidaEm > ValidadeConfiguracao)
        {
            _configuracao = await store.ConfiguracaoAsync(ct);
            _configuracaoLidaEm = agora;
        }

        return _configuracao;
    }
}

/// <summary>Resultado de uma gravação da fila.</summary>
/// <param name="Gravados">Erros gravados.</param>
/// <param name="Ignorados">Não gravados de propósito (opção desligada ou tabela ainda inexistente).</param>
/// <param name="Falhas">Não gravados por erro do banco.</param>
/// <param name="UltimaFalha">A última exceção do banco, para o log.</param>
public sealed record ResultadoGravacaoErros(int Gravados, int Ignorados, int Falhas, Exception? UltimaFalha);

/// <summary>Monta o <see cref="ErroCapturado"/> a partir de um evento de log de nível Error (usado pelos sinks da Web e do serviço).</summary>
public static class ErroDeLog
{
    /// <summary>Contextos que nunca viram erro registrado: a própria gravação (evita laço) e o que a tela de erro já registra.</summary>
    private static readonly string[] ContextosIgnorados =
    [
        "IntegracaoSolidesDP.Worker.Erros",
        "IntegracaoSolidesDP.Web.Infrastructure.GravadorErros",
        "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
    ];

    private const string ContextoPipeline = "IntegracaoSolidesDP.Worker.Pipeline.SyncPipeline";

    /// <summary>"Execução {RunId} abortada: ..." = DP, banco ou configuração fora (SyncAbortedException), não defeito.</summary>
    private const string TemplateAbortada = "Execução {RunId} abortada";

    /// <summary>Versão do produto (sem o hash do commit): a Web, o serviço e o Core saem do mesmo build.</summary>
    public static string Versao { get; } =
        typeof(ErroDeLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "desconhecida";

    /// <summary>O evento deste contexto não deve ser registrado.</summary>
    public static bool Ignorar(string? contexto) =>
        contexto is not null && ContextosIgnorados.Any(c => contexto.StartsWith(c, StringComparison.Ordinal));

    /// <summary>Monta o erro capturado.</summary>
    /// <param name="origem"><see cref="OrigensErro"/>.</param>
    /// <param name="contexto">SourceContext do log (classe que registrou).</param>
    /// <param name="template">Template da mensagem ("Execução {RunId} abortada: {Error}").</param>
    /// <param name="mensagem">Mensagem já com os valores.</param>
    /// <param name="excecao">Exceção do evento, quando há.</param>
    /// <param name="cdempresa">Propriedade Empresa do evento, quando há.</param>
    /// <param name="quando">Hora do evento.</param>
    public static ErroCapturado Criar(
        string origem, string? contexto, string template, string mensagem, Exception? excecao, int? cdempresa, DateTimeOffset quando)
    {
        ArgumentNullException.ThrowIfNull(template);
        var raiz = excecao is AggregateException { InnerExceptions.Count: 1 } agregada ? agregada.InnerExceptions[0] : excecao;
        var texto = raiz is null || mensagem.Contains(raiz.Message, StringComparison.Ordinal) ? mensagem : $"{mensagem}: {raiz.Message}";
        return new ErroCapturado
        {
            Origem = origem,
            Fonte = contexto,
            Tipo = raiz?.GetType().FullName,
            Mensagem = texto,
            Detalhe = excecao?.ToString(),
            Cdempresa = cdempresa,
            Maquina = Environment.MachineName,
            Versao = Versao,
            Quando = quando,
            ExecucaoCaida = string.Equals(contexto, ContextoPipeline, StringComparison.Ordinal)
                            && template.StartsWith(TemplateAbortada, StringComparison.Ordinal),
        };
    }
}
