using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Sql;

/// <summary>Limpeza do histórico e sinal de vida do serviço no SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlLimpezaTests(SqlServerFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Inicio = new(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(-3));
    private readonly FakeTimeProvider _clock = new(Inicio);
    private SqlStateStore _state = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _state = new SqlStateStore(db.Connections, _clock);
        await _state.EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Old_runs_are_deleted_in_batches_with_items_and_reports_keeping_the_last_of_each_company_and_running_ones()
    {
        var antiga1 = await RunAsync(15, dryRun: false);
        await _state.AddItemsAsync(antiga1, [new RunItem("employee", "15-1", "create", "failed", null, "x")], Ct);
        await _state.SaveRunReportAsync(antiga1, "vinculacao", "a.csv", "conteudo", Ct);
        _clock.Advance(TimeSpan.FromDays(1));
        await RunAsync(15, dryRun: false); // outra antiga, também apagada
        var ultimaSimulacao15 = await RunAsync(15, dryRun: true);
        var unica14 = await RunAsync(14, dryRun: false);
        _clock.Advance(TimeSpan.FromDays(1));
        var emAndamento = await _state.StartRunAsync("default", 15, dryRun: false, "teste", Ct);
        _clock.Advance(TimeSpan.FromDays(300));
        var recente = await RunAsync(15, dryRun: false);
        var store = new SqlLimpezaStore(db.Connections);
        var limite = Inicio.AddDays(200);

        var lotes = new List<int>();
        int apagadas;
        do
        {
            apagadas = await store.ApagarExecucoesAsync(limite, lote: 1, Ct);
            lotes.Add(apagadas);
        }
        while (apagadas > 0);

        lotes.Should().Equal(1, 1, 0);
        (await db.QueryAsync<Guid>("SELECT run_id FROM solidesdp.runs")).Should()
            .BeEquivalentTo([ultimaSimulacao15, unica14, emAndamento, recente], "a última de cada empresa e a em andamento ficam");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.run_items")).Should().Equal(0);
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.run_relatorio")).Should().Equal(0);
    }

    [Fact]
    public async Task Old_sent_emails_are_deleted()
    {
        await db.ExecuteAsync("""
            INSERT INTO solidesdp.notificacao_envio (enviado_em, tipo, para, assunto, ok)
            VALUES (@Velho, 'teste', 'a@b.c', 'velho', 1), (@Novo, 'teste', 'a@b.c', 'novo', 1)
            """, new { Velho = Inicio, Novo = Inicio.AddDays(10) });

        var apagados = await new SqlLimpezaStore(db.Connections).ApagarEnviosAsync(Inicio.AddDays(1), Ct);

        apagados.Should().Be(1);
        (await db.QueryAsync<string>("SELECT assunto FROM solidesdp.notificacao_envio")).Should().Equal("novo");
    }

    [Fact]
    public async Task Service_signal_is_one_row_per_instance_and_machine_newest_first()
    {
        var store = new SqlSinalServicoStore(db.Connections);
        await store.RegistrarAsync(Sinal("PC-EDUARDO", Inicio), Ct);
        await store.RegistrarAsync(Sinal("SRVHOMOLOGACAO", Inicio.AddMinutes(1)), Ct);
        await store.RegistrarAsync(Sinal("SRVHOMOLOGACAO", Inicio.AddMinutes(2)) with { Versao = "1.0.3" }, Ct);

        var sinais = await store.ListarAsync(Ct);

        sinais.Select(s => (s.Maquina, s.Versao)).Should().Equal(("SRVHOMOLOGACAO", "1.0.3"), ("PC-EDUARDO", "1.0.2"));
        sinais[0].UltimoSinal.Should().Be(Inicio.AddMinutes(2));

        (await store.ApagarAntigosAsync(Inicio.AddMinutes(1), Ct)).Should().Be(1);
        (await store.ListarAsync(Ct)).Should().ContainSingle().Which.Maquina.Should().Be("SRVHOMOLOGACAO");
    }

    [Fact]
    public async Task Listing_signals_before_the_table_exists_returns_nothing()
    {
        await db.ExecuteAsync("DROP TABLE solidesdp.servico_sinal");

        (await new SqlSinalServicoStore(db.Connections).ListarAsync(Ct)).Should().BeEmpty();
    }

    private async Task<Guid> RunAsync(int cdempresa, bool dryRun)
    {
        var id = await _state.StartRunAsync("default", cdempresa, dryRun, "teste", Ct);
        await _state.FinishRunAsync(id, RunStatuses.Completed, "{}", null, Ct);
        return id;
    }

    private static SinalServico Sinal(string maquina, DateTimeOffset quando) => new()
    {
        Instancia = "default", Maquina = maquina, Versao = "1.0.2", Pid = 42, IniciadoEm = Inicio, UltimoSinal = quando,
    };
}
