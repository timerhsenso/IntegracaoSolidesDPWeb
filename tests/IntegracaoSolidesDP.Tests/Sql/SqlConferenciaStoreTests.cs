using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlConferenciaStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 12, 6, 0, 0, TimeSpan.FromHours(-3));
    private SqlConferenciaStore _store = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _store = new SqlConferenciaStore(db.Connections);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Conference_is_saved_with_its_lines_and_listed_without_them()
    {
        var id = await _store.SalvarAsync(Conferencia(15, Inicio) with
        {
            Conferidos = 33, Pessoas = 1, Divergencias = 1,
            Linhas = [new DivergenciaCampo { Rotulo = "15-00000001", Nome = "MARIA", Cpf = TestData.Cpf1, CpfMascarado = "***", CadastroDp = 9, Campo = CamposConferencia.Nome, RhSenso = "MARIA SOUZA", Dp = "MARIA" }],
        }, Ct);

        var lida = (await _store.ObterAsync(id, Ct))!;
        lida.Should().Match<Conferencia>(c => c.Cdempresa == 15 && c.Conferidos == 33 && c.Pessoas == 1 && c.IniciadaEm == Inicio);
        lida.Linhas.Should().ContainSingle().Which.Should().Match<DivergenciaCampo>(l => l.Cpf == TestData.Cpf1 && l.Dp == "MARIA" && l.CadastroDp == 9);
        (await _store.RecentesAsync(10, Ct)).Should().ContainSingle().Which.Linhas.Should().BeEmpty();
        (await _store.UltimaAsync("default", 15, Ct)).Should().Be(Inicio);
        (await _store.UltimaAsync("default", 14, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Old_conferences_are_deleted_keeping_the_last_of_each_company()
    {
        await _store.SalvarAsync(Conferencia(15, Inicio), Ct);
        await _store.SalvarAsync(Conferencia(15, Inicio.AddDays(7)), Ct);
        await _store.SalvarAsync(Conferencia(14, Inicio), Ct);

        var apagadas = await _store.ApagarAntigasAsync(Inicio.AddDays(30), Ct);

        apagadas.Should().Be(1);
        (await _store.RecentesAsync(10, Ct)).Select(c => (c.Cdempresa, c.IniciadaEm)).Should().BeEquivalentTo(new[] { (15, Inicio.AddDays(7)), (14, Inicio) });
    }

    [Fact]
    public async Task Resend_clears_the_hash_of_the_chosen_people_of_that_company_only()
    {
        var state = new SqlStateStore(db.Connections, TimeProvider.System);
        foreach (var (empresa, cpf) in new[] { (15, TestData.Cpf1), (15, TestData.Cpf2), (14, TestData.Cpf1) })
        {
            await state.UpsertEntityStateAsync(empresa, new EntityState
            {
                EntityType = EntityTypes.Employee, ExternalId = cpf, RemoteId = 9, PayloadHash = new string('a', 64), Status = EntityStatuses.Synced,
                Matricula = "00000001", Cdfilial = 1,
            }, Ct);
        }

        var marcadas = await _store.MarcarReenvioAsync(15, [TestData.Cpf1], Ct);

        marcadas.Should().Be(1);
        (await state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().BeNull();
        (await state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf2].PayloadHash.Should().NotBeNull();
        (await state.LoadEntityStatesAsync(14, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().NotBeNull();
    }

    private static Conferencia Conferencia(int cdempresa, DateTimeOffset quando) => new()
    {
        Instancia = "default", Cdempresa = cdempresa, IniciadaEm = quando, ConcluidaEm = quando, Gatilho = GatilhosConferencia.Agendada,
    };
}
