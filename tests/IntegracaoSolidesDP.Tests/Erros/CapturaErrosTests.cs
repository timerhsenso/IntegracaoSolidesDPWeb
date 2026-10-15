using IntegracaoSolidesDP.Worker.Erros;
using Serilog;
using Serilog.Core;

namespace IntegracaoSolidesDP.Tests.Erros;

/// <summary>Do log ao registro: só nível Error, sem laço, execução que caiu conforme a configuração e fila limitada.</summary>
public sealed class CapturaErrosTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(-3));
    private readonly FilaErros _fila = new();
    private readonly MemoryErroStore _store = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_errors_are_captured_with_the_company_and_the_exception()
    {
        using var log = CriarLog();
        var pipeline = log.ForContext(Constants.SourceContextPropertyName, "IntegracaoSolidesDP.Worker.Pipeline.ConferenciaCampos");

        pipeline.Information("Empresa {Empresa}: tudo certo", 15);
        pipeline.Warning("Empresa {Empresa}: aviso", 15);
        pipeline.Error(new InvalidOperationException("Sequence contains no elements"), "Conferência da empresa {Empresa} falhou", 15);
        var resultado = await _fila.GravarAsync(_store, Agora, Ct);

        resultado.Gravados.Should().Be(1);
        _store.Registrados.Should().ContainSingle().Which.Should().Match<ErroCapturado>(e =>
            e.Origem == OrigensErro.Servico && e.Cdempresa == 15 && e.Tipo == "System.InvalidOperationException"
            && e.Mensagem == "Conferência da empresa 15 falhou: Sequence contains no elements"
            && e.Fonte == "IntegracaoSolidesDP.Worker.Pipeline.ConferenciaCampos"
            && e.Detalhe!.Contains("Sequence contains no elements", StringComparison.Ordinal) && !e.ExecucaoCaida);
    }

    [Fact]
    public async Task The_recording_itself_and_what_the_error_page_records_are_not_captured()
    {
        using var log = CriarLog();

        log.ForContext(Constants.SourceContextPropertyName, "IntegracaoSolidesDP.Worker.Erros.GravadorErros").Error("falhou gravar");
        log.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").Error("tela");
        await _fila.GravarAsync(_store, Agora, Ct);

        _store.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task An_aborted_run_is_recorded_only_with_the_option_on()
    {
        using var log = CriarLog();
        var pipeline = log.ForContext(Constants.SourceContextPropertyName, "IntegracaoSolidesDP.Worker.Pipeline.SyncPipeline");

        pipeline.Error("Execução {RunId} abortada: {Error}", Guid.NewGuid(), "Sólides DP fora do ar");
        pipeline.Error(new InvalidOperationException("bug"), "Execução {RunId} falhou", Guid.NewGuid());
        _store.Configuracao = new ConfiguracaoErros { ExecucaoCaida = false };
        var desligado = await _fila.GravarAsync(_store, Agora, Ct);

        desligado.Should().Match<ResultadoGravacaoErros>(r => r.Gravados == 1 && r.Ignorados == 1);
        _store.Registrados.Should().ContainSingle().Which.Mensagem.Should().Contain("falhou", "defeito sempre é registrado");

        _store.Configuracao = new ConfiguracaoErros();
        pipeline.Error("Execução {RunId} abortada: {Error}", Guid.NewGuid(), "Sólides DP fora do ar");
        (await _fila.GravarAsync(_store, Agora.AddMinutes(2), Ct)).Gravados.Should().Be(1, "a configuração é relida depois de um minuto");
        _store.Registrados.Last().ExecucaoCaida.Should().BeTrue();
    }

    [Fact]
    public async Task A_database_failure_does_not_stop_the_recording_and_a_full_queue_drops()
    {
        for (var i = 0; i < FilaErros.Capacidade + 5; i++)
        {
            _fila.Enfileirar(new ErroCapturado { Origem = OrigensErro.Web, Mensagem = $"erro {i}" });
        }

        _store.Falhar = true;
        var resultado = await _fila.GravarAsync(_store, Agora, Ct);

        _fila.Descartados.Should().Be(5);
        resultado.Falhas.Should().Be(FilaErros.Capacidade);
        resultado.UltimaFalha.Should().NotBeNull();
    }

    private Logger CriarLog() => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CapturaErrosSink(_fila)).CreateLogger();
}

/// <summary>Erros em memória (o SQL tem testes próprios).</summary>
internal sealed class MemoryErroStore : IErroStore
{
    public List<ErroCapturado> Registrados { get; } = [];

    /// <summary>Erros já gravados, para os avisos por e-mail.</summary>
    public List<ErroAplicacao> Erros { get; } = [];

    public ConfiguracaoErros Configuracao { get; set; } = new();

    public bool Falhar { get; set; }

    /// <summary>De onde vêm os envios do dia (o MemoryNotificacaoStore).</summary>
    public Func<IEnumerable<IntegracaoSolidesDP.Worker.Notificacoes.EnvioNotificacao>> Envios { get; set; } = () => [];

    public Task<long?> RegistrarAsync(ErroCapturado erro, CancellationToken ct)
    {
        if (Falhar)
        {
            throw new InvalidOperationException("banco fora do ar");
        }

        Registrados.Add(erro);
        return Task.FromResult<long?>(Registrados.Count);
    }

    public Task<IReadOnlyList<ErroAplicacao>> ListarAsync(string? situacao, string? origem, int quantidade, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErroAplicacao>>(Erros.ToList());

    public Task<ErroAplicacao?> ObterAsync(long id, CancellationToken ct) => Task.FromResult(Erros.FirstOrDefault(e => e.Id == id));

    public Task<bool> MudarSituacaoAsync(long id, string situacao, string usuario, CancellationToken ct) => Task.FromResult(false);

    public Task<int> AbertosAsync(CancellationToken ct) => Task.FromResult(Registrados.Count);

    public Task<int> ApagarAntigosAsync(DateTimeOffset limite, CancellationToken ct) => Task.FromResult(0);

    public Task<ConfiguracaoErros> ConfiguracaoAsync(CancellationToken ct) => Task.FromResult(Configuracao);

    public Task SalvarConfiguracaoAsync(ConfiguracaoErros configuracao, string usuario, CancellationToken ct)
    {
        Configuracao = configuracao;
        return Task.CompletedTask;
    }

    public Task<bool> ComentarAsync(long id, string usuario, string? comentario, bool pedirEnvio, CancellationToken ct)
    {
        var indice = Erros.FindIndex(e => e.Id == id);
        if (indice < 0)
        {
            return Task.FromResult(false);
        }

        var erro = Erros[indice];
        Erros[indice] = erro with
        {
            Comentario = comentario is null ? erro.Comentario : erro.Comentario is null ? comentario : erro.Comentario + "\n" + comentario,
            EnvioPedidoEm = pedirEnvio ? DateTimeOffset.UtcNow : erro.EnvioPedidoEm,
            EnvioPedidoPor = pedirEnvio ? usuario : erro.EnvioPedidoPor,
        };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ErroAplicacao>> ParaAvisarAsync(DateTimeOffset recentesDesde, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ErroAplicacao>>(Erros
            .Where(e => e.EnvioPedidoEm is not null
                        || (e.Situacao == SituacoesErro.Aberto && e.UltimaVez >= recentesDesde && (e.UltimoAviso is null || e.Ocorrencias > e.OcorrenciasNoAviso)))
            .OrderBy(e => e.EnvioPedidoEm is null ? 1 : 0).ThenBy(e => e.PrimeiraVez).ThenBy(e => e.Id)
            .ToList());

    public Task MarcarAvisadosAsync(IReadOnlyCollection<(long Id, int Ocorrencias)> avisados, DateTimeOffset quando, CancellationToken ct)
    {
        foreach (var (id, ocorrencias) in avisados)
        {
            var indice = Erros.FindIndex(e => e.Id == id);
            Erros[indice] = Erros[indice] with { UltimoAviso = quando, OcorrenciasNoAviso = ocorrencias, EnvioPedidoEm = null, EnvioPedidoPor = null };
        }

        return Task.CompletedTask;
    }

    public Task<EnviosDeErro> EnviosDesdeAsync(DateTimeOffset desde, CancellationToken ct)
    {
        var doDia = Envios().Where(e => e.EnviadoEm >= desde).ToList();
        return Task.FromResult(new EnviosDeErro(
            doDia.Count(e => e.Ok && IntegracaoSolidesDP.Worker.Notificacoes.TiposNotificacao.ContamNoLimite.Contains(e.Tipo)),
            doDia.Any(e => e.Tipo == IntegracaoSolidesDP.Worker.Notificacoes.TiposNotificacao.ErroLimite)));
    }
}
