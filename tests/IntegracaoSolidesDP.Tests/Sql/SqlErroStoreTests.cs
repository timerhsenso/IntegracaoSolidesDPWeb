using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Management;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlErroStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(-3));
    private readonly FakeTimeProvider _clock = new(Inicio);
    private SqlErroStore _store = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _store = new SqlErroStore(db.Connections, _clock);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Without_the_tables_nothing_is_recorded_and_the_defaults_apply()
    {
        (await _store.RegistrarAsync(Erro("falhou"), Ct)).Should().BeNull("sem a gestão a tabela não existe");
        (await _store.ListarAsync(null, null, 10, Ct)).Should().BeEmpty();
        (await _store.ObterAsync(1, Ct)).Should().BeNull();
        (await _store.AbertosAsync(Ct)).Should().Be(0);
        (await _store.ApagarAntigosAsync(Inicio, Ct)).Should().Be(0);
        (await _store.ConfiguracaoAsync(Ct)).Should().Be(new ConfiguracaoErros());
    }

    [Fact]
    public async Task The_same_error_adds_up_in_one_line_with_the_latest_data_and_cleaned_text()
    {
        await CriarTabelasAsync();

        var primeiro = await _store.RegistrarAsync(Erro($"Execução {Guid.NewGuid()} falhou para o CPF 52998224725") with { Usuario = "ana" }, Ct);
        _clock.Advance(TimeSpan.FromMinutes(30));
        var segundo = await _store.RegistrarAsync(Erro($"Execução {Guid.NewGuid()} falhou para o CPF 52998224725") with { Usuario = "carlos", Quando = _clock.GetUtcNow() }, Ct);
        var outro = await _store.RegistrarAsync(Erro("outro erro") with { Origem = OrigensErro.Web }, Ct);

        segundo.Should().Be(primeiro);
        outro.Should().NotBe(primeiro);
        var erro = (await _store.ObterAsync(primeiro!.Value, Ct))!;
        erro.Should().Match<ErroAplicacao>(e =>
            e.Ocorrencias == 2 && e.Usuario == "carlos" && e.Situacao == SituacoesErro.Aberto
            && e.PrimeiraVez == Inicio && e.UltimaVez == Inicio.AddMinutes(30)
            && e.Mensagem.Contains("529.***.***-25", StringComparison.Ordinal) && !e.Mensagem.Contains("52998224725", StringComparison.Ordinal)
            && e.Detalhe == "pilha" && e.Codigo == CodigoErro.De(primeiro.Value));
        (await _store.ListarAsync(null, null, 10, Ct)).Select(e => e.Id).Should().Equal(primeiro.Value, outro!.Value);
        (await _store.ListarAsync(null, OrigensErro.Web, 10, Ct)).Should().ContainSingle().Which.Detalhe.Should().BeNull("a lista não traz o detalhe");
        (await _store.AbertosAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Silenced_keeps_counting_and_a_resolved_error_that_happens_again_reopens()
    {
        await CriarTabelasAsync();
        var silenciado = (await _store.RegistrarAsync(Erro("conhecido"), Ct))!.Value;
        var resolvido = (await _store.RegistrarAsync(Erro("corrigido"), Ct))!.Value;

        (await _store.MudarSituacaoAsync(silenciado, SituacoesErro.Silenciado, "carlos", Ct)).Should().BeTrue();
        (await _store.MudarSituacaoAsync(silenciado, SituacoesErro.Silenciado, "carlos", Ct)).Should().BeFalse("já estava silenciado");
        (await _store.MudarSituacaoAsync(resolvido, SituacoesErro.Resolvido, "carlos", Ct)).Should().BeTrue();
        (await _store.AbertosAsync(Ct)).Should().Be(0);

        await _store.RegistrarAsync(Erro("conhecido"), Ct);
        await _store.RegistrarAsync(Erro("corrigido"), Ct);

        (await _store.ObterAsync(silenciado, Ct))!.Should().Match<ErroAplicacao>(e =>
            e.Situacao == SituacoesErro.Silenciado && e.Ocorrencias == 2 && e.SituacaoPor == "carlos");
        (await _store.ObterAsync(resolvido, Ct))!.Should().Match<ErroAplicacao>(e =>
            e.Situacao == SituacoesErro.Aberto && e.Ocorrencias == 2 && e.SituacaoPor == null && e.SituacaoEm == null);
        (await _store.ListarAsync(SituacoesErro.Aberto, null, 10, Ct)).Should().ContainSingle().Which.Id.Should().Be(resolvido);
    }

    [Fact]
    public async Task Configuration_is_one_row_and_old_errors_are_deleted()
    {
        await CriarTabelasAsync();
        var velho = (await _store.RegistrarAsync(Erro("velho"), Ct))!.Value;
        await _store.RegistrarAsync(Erro("novo") with { Quando = Inicio.AddDays(100) }, Ct);

        await _store.SalvarConfiguracaoAsync(new ConfiguracaoErros { OperadorVe = true, RetencaoDias = 30, NomeCliente = "  ADN  " }, "carlos", Ct);
        await _store.SalvarConfiguracaoAsync((await _store.ConfiguracaoAsync(Ct)) with { ExecucaoCaida = false }, "ana", Ct);
        var apagados = await _store.ApagarAntigosAsync(Inicio.AddDays(50), Ct);

        (await _store.ConfiguracaoAsync(Ct)).Should().Match<ConfiguracaoErros>(c =>
            c.OperadorVe && c.RetencaoDias == 30 && !c.ExecucaoCaida && c.NomeCliente == "ADN" && c.AtualizadoPor == "ana"
            && c.ModoEnvio == ModosEnvioErro.Automatico);
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.erro_config")).Should().Equal(1);
        apagados.Should().Be(1);
        (await _store.ObterAsync(velho, Ct)).Should().BeNull();
    }

    private Task CriarTabelasAsync() => new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);

    private static ErroCapturado Erro(string mensagem) => new()
    {
        Origem = OrigensErro.Servico,
        Fonte = "IntegracaoSolidesDP.Worker.Pipeline.SyncPipeline",
        Tipo = "System.InvalidOperationException",
        Mensagem = mensagem,
        Detalhe = "pilha",
        Maquina = "SRVHOMOLOGACAO",
        Versao = "1.0.7",
        Quando = Inicio,
    };
}
