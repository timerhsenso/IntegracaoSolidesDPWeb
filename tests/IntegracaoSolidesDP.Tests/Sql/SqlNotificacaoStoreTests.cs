using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Time.Testing;

namespace IntegracaoSolidesDP.Tests.Sql;

[Collection(SqlServerCollection.Name)]
public sealed class SqlNotificacaoStoreTests(SqlServerFixture db) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.FromHours(-3)));
    private SqlNotificacaoStore _store = null!;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _store = new SqlNotificacaoStore(db.Connections, _clock);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Configuration_is_a_single_row_and_the_password_is_kept_unless_changed()
    {
        (await _store.ConfiguracaoAsync(Ct)).Should().BeNull();
        var config = new ConfiguracaoNotificacao
        {
            Ativo = true,
            Servidor = "smtp.skymail.net.br",
            Porta = 587,
            Usuario = "adnserv@adn.com.br",
            SenhaCifrada = [1, 2, 3],
            Remetente = "adnserv@adn.com.br",
            HoraResumo = new TimeSpan(7, 30, 0),
            LembreteHoras = 12,
            UrlWeb = "https://srv:8443",
        };

        await _store.SalvarConfiguracaoAsync(config, trocarSenha: true, "carlos", Ct);
        await _store.SalvarConfiguracaoAsync(config with { SenhaCifrada = null, Porta = 2525 }, trocarSenha: false, "carlos", Ct);
        await _store.MarcarResumoAsync(_clock.GetUtcNow(), Ct);

        var lida = (await _store.ConfiguracaoAsync(Ct))!;
        lida.Porta.Should().Be(2525);
        lida.SenhaCifrada.Should().Equal(1, 2, 3);
        lida.HoraResumo.Should().Be(new TimeSpan(7, 30, 0));
        lida.LembreteHoras.Should().Be(12);
        lida.UltimoResumo.Should().Be(_clock.GetUtcNow());
        lida.AtualizadoPor.Should().Be("carlos");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.notificacao_config")).Should().Equal(1);

        await _store.SalvarConfiguracaoAsync(config with { SenhaCifrada = null }, trocarSenha: true, "carlos", Ct);
        (await _store.ConfiguracaoAsync(Ct))!.SenhaCifrada.Should().BeNull("apagar a senha é trocar por nada");
    }

    [Fact]
    public async Task Recipients_are_added_and_changed_never_deleted()
    {
        var id = await _store.SalvarDestinatarioAsync(
            new DestinatarioNotificacao { Email = " rh15@adn.com.br ", Cdempresa = 15, RecebeResumo = true }, "carlos", Ct);
        await _store.SalvarDestinatarioAsync(
            new DestinatarioNotificacao { Id = id, Email = "rh15@adn.com.br", Cdempresa = 15, RecebeResumo = true, Ativo = false }, "carlos", Ct);
        await _store.SalvarDestinatarioAsync(new DestinatarioNotificacao { Email = "ti@adn.com.br", RecebeFalhas = true }, "carlos", Ct);

        var todos = await _store.DestinatariosAsync(Ct);

        todos.Select(d => (d.Email, d.Cdempresa, d.Ativo)).Should().Equal(("ti@adn.com.br", null, true), ("rh15@adn.com.br", 15, false));
    }

    [Fact]
    public async Task Pending_items_stay_open_resolve_when_gone_and_reopen_as_new()
    {
        var segunda = _clock.GetUtcNow();
        await _store.AtualizarPendenciasAsync(15, [new("employee|15-1|cpf_desligado_no_dp", "15-1", "a"), new("employee|15-2|x", "15-2", "b")], segunda, Ct);
        await _store.AtualizarPendenciasAsync(14, [new("employee|14-1|x", "14-1", "c")], segunda, Ct);

        _clock.Advance(TimeSpan.FromHours(1));
        await _store.AtualizarPendenciasAsync(15, [new("employee|15-1|cpf_desligado_no_dp", "15-1", "a2")], _clock.GetUtcNow(), Ct);

        var abertas = await _store.PendenciasAbertasAsync(Ct);
        abertas.Select(p => p.Rotulo).Should().BeEquivalentTo(["15-1", "14-1"], "15-2 sumiu da execução: resolvida; a empresa 14 não é afetada");
        abertas.Single(p => p.Rotulo == "15-1").Should().Match<PendenciaAberta>(p => p.PrimeiraVez == segunda && p.Mensagem == "a2");

        _clock.Advance(TimeSpan.FromHours(1));
        var reaberta = _clock.GetUtcNow();
        await _store.AtualizarPendenciasAsync(15, [new("employee|15-1|cpf_desligado_no_dp", "15-1", "a2"), new("employee|15-2|x", "15-2", "b")], reaberta, Ct);

        (await _store.PendenciasAbertasAsync(Ct)).Single(p => p.Rotulo == "15-2").PrimeiraVez.Should().Be(reaberta, "voltou depois de resolvida: conta como nova");
    }

    [Fact]
    public async Task State_and_sent_emails_are_recorded()
    {
        await _store.SalvarEstadoAsync(new EstadoNotificacao { Cdempresa = 15, EmFalha = true, FalhaDesde = _clock.GetUtcNow(), UltimoAviso = _clock.GetUtcNow() }, Ct);
        await _store.SalvarEstadoAsync(new EstadoNotificacao { Cdempresa = 15, EmFalha = false, UltimoAviso = _clock.GetUtcNow() }, Ct);
        await _store.RegistrarEnvioAsync(new EnvioNotificacao
        {
            EnviadoEm = _clock.GetUtcNow(), Tipo = TiposNotificacao.Teste, Para = "ti@adn.com.br", Assunto = "teste", Ok = false, Erro = "auth",
        }, Ct);

        (await _store.EstadoAsync(15, Ct))!.EmFalha.Should().BeFalse();
        (await _store.EstadoAsync(14, Ct)).Should().BeNull();
        (await _store.EnviosRecentesAsync(10, Ct)).Should().ContainSingle().Which.Erro.Should().Be("auth");
    }
}
