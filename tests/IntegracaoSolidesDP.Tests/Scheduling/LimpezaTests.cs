using IntegracaoSolidesDP.Tests.Pipeline;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Scheduling;
using IntegracaoSolidesDP.Worker.Source;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Scheduling;

/// <summary>Quando a limpeza diária roda e o que ela apaga na pasta de relatórios.</summary>
public sealed class LimpezaTests : IAsyncLifetime
{
    // 02:30 em Salvador.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 2, 30, 0, TimeSpan.FromHours(-3)));
    private readonly FakeLimpezaStore _store = new();
    private readonly string _pasta = Directory.CreateTempSubdirectory("solidesdp-limpeza").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_pasta, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Runs_once_a_day_from_the_configured_hour()
    {
        var limpeza = Criar(new SyncOptions { LimpezaHora = 3 });

        (await limpeza.LimparSeDevidoAsync(Ct)).Should().BeNull("ainda não são 3h");
        _clock.Advance(TimeSpan.FromHours(1));
        var primeira = await limpeza.LimparSeDevidoAsync(Ct);
        var repetida = await limpeza.LimparSeDevidoAsync(Ct);
        _clock.Advance(TimeSpan.FromDays(1));
        var amanha = await limpeza.LimparSeDevidoAsync(Ct);

        primeira.Should().NotBeNull();
        repetida.Should().BeNull("uma vez por dia");
        amanha.Should().NotBeNull();
        _store.Limites.Should().HaveCount(2);
    }

    [Fact]
    public async Task Deletes_old_runs_in_batches_and_old_report_files_only()
    {
        _store.Lotes.Enqueue(Limpeza.Lote);
        _store.Lotes.Enqueue(5);
        var velho = Arquivo("run-velho.csv", dias: 31);
        var velhoJson = Arquivo("vinculacao-velho.json", dias: 40);
        var novo = Arquivo("run-novo.csv", dias: 1);
        var outro = Arquivo("anotacao.txt", dias: 90);
        var limpeza = Criar(new SyncOptions { RetencaoExecucoesDias = 180, RetencaoArquivosDias = 30 });

        var resultado = await limpeza.LimparAsync(new SyncOptions { RetencaoExecucoesDias = 180, RetencaoArquivosDias = 30 }, Ct);

        resultado.Should().Be(new ResultadoLimpeza(Limpeza.Lote + 5, 0, 0, 2));
        _store.Limites.Should().AllBeEquivalentTo(_clock.GetUtcNow().AddDays(-180));
        File.Exists(velho).Should().BeFalse();
        File.Exists(velhoJson).Should().BeFalse();
        File.Exists(novo).Should().BeTrue();
        File.Exists(outro).Should().BeTrue("só CSV e JSON são relatórios");
    }

    [Fact]
    public async Task Zero_days_keeps_everything()
    {
        var velho = Arquivo("run-velho.csv", dias: 400);
        var limpeza = Criar(new SyncOptions { RetencaoExecucoesDias = 0, RetencaoArquivosDias = 0 });

        var resultado = await limpeza.LimparAsync(new SyncOptions { RetencaoExecucoesDias = 0, RetencaoArquivosDias = 0 }, Ct);

        resultado.Should().Be(new ResultadoLimpeza(0, 0, 0, 0));
        _store.Limites.Should().BeEmpty();
        File.Exists(velho).Should().BeTrue();
    }

    private Limpeza Criar(SyncOptions regras)
    {
        regras.ReportDirectory = _pasta;
        return new Limpeza(
            _store,
            new SqlSinalServicoStore(new ConnectionFactory("Server=nao-usado")),
            new SqlConferenciaStore(new ConnectionFactory("Server=nao-usado")),
            new MemoryManagement(),
            Options.Create(regras),
            Options.Create(new ManagementOptions { Habilitada = false }),
            TestData.Dates,
            _clock,
            NullLogger<Limpeza>.Instance);
    }

    private string Arquivo(string nome, int dias)
    {
        var caminho = Path.Combine(_pasta, nome);
        File.WriteAllText(caminho, "x");
        File.SetLastWriteTimeUtc(caminho, _clock.GetUtcNow().UtcDateTime.AddDays(-dias));
        return caminho;
    }

    private sealed class FakeLimpezaStore : ILimpezaStore
    {
        public Queue<int> Lotes { get; } = new();

        public List<DateTimeOffset> Limites { get; } = [];

        public Task<int> ApagarExecucoesAsync(DateTimeOffset limite, int lote, CancellationToken ct)
        {
            Limites.Add(limite);
            return Task.FromResult(Lotes.Count > 0 ? Lotes.Dequeue() : 0);
        }

        public Task<int> ApagarEnviosAsync(DateTimeOffset limite, CancellationToken ct) => Task.FromResult(0);
    }
}
