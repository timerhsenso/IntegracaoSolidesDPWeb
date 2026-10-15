using System.Net;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Tela Erros da aplicação: Admin sempre, Operador só se configurado; silenciar e as opções ficam na auditoria.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ErrosWebTests(SqlServerFixture db) : IAsyncLifetime
{
    private WebHarness _web = null!;
    private long _id;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await new SqlStateStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _id = (await new SqlErroStore(db.Connections, TimeProvider.System).RegistrarAsync(new ErroCapturado
        {
            Origem = OrigensErro.Servico,
            Fonte = "IntegracaoSolidesDP.Worker.Pipeline.ConferenciaCampos",
            Tipo = "System.InvalidOperationException",
            Mensagem = "Conferência da empresa 15 falhou: CPF 52998224725 sem cadastro",
            Detalhe = "System.InvalidOperationException: pilha do erro",
            Cdempresa = 15,
            Quando = DateTimeOffset.UtcNow,
        }, Ct))!.Value;
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Admin_sees_the_error_with_the_code_and_the_detail_and_silences_it()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);
        var codigo = CodigoErro.De(_id);

        var lista = WebUtility.HtmlDecode(await client.GetStringAsync("/Erros", Ct));
        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Erros/Detalhe/{_id}", Ct));

        lista.Should().Contain(codigo).And.Contain("Conferência da empresa 15 falhou").And.Contain("529.***.***-25").And.NotContain("52998224725");
        lista.Should().Contain("Erros da aplicação", "o Admin tem o item no menu");
        detalhe.Should().Contain("pilha do erro").And.Contain("Silenciar");

        var silenciar = await WebHarness.PostFormAsync(client, $"/Erros/Detalhe/{_id}", $"/Erros/Silenciar/{_id}", []);

        silenciar.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await new SqlErroStore(db.Connections, TimeProvider.System).ObterAsync(_id, Ct))!.Situacao.Should().Be(SituacoesErro.Silenciado);
        (await db.QueryAsync<string>("SELECT detalhe FROM solidesdp.auditoria WHERE usuario = @login AND acao = @acao",
            new { login, acao = AcoesAuditoria.ErroSilenciado })).Should().ContainSingle().Which.Should().StartWith(codigo);
    }

    [Fact]
    public async Task Admin_asks_to_send_the_error_with_a_comment_and_registers_the_support_email()
    {
        var login = await _web.CriarUsuarioAsync(Perfis.Admin);
        var client = await _web.EntrarAsync(login);

        var destinatario = await WebHarness.PostFormAsync(client, "/Notificacoes", "/Notificacoes/Destinatario", new()
        {
            ["NovoDestinatario.Email"] = "suporte@rhsenso.com.br",
            ["NovoDestinatario.RecebeFalhas"] = "false",
            ["NovoDestinatario.RecebeResumo"] = "false",
            ["NovoDestinatario.RecebeErros"] = "true",
        });
        var detalhe = WebUtility.HtmlDecode(await client.GetStringAsync($"/Erros/Detalhe/{_id}", Ct));
        var enviar = await WebHarness.PostFormAsync(client, $"/Erros/Detalhe/{_id}", $"/Erros/Enviar/{_id}", new()
        {
            ["comentario"] = "acontece ao salvar a empresa 15",
        });

        destinatario.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await new IntegracaoSolidesDP.Worker.Notificacoes.SqlNotificacaoStore(db.Connections, TimeProvider.System).DestinatariosAsync(Ct))
            .Should().ContainSingle().Which.Should().Match<IntegracaoSolidesDP.Worker.Notificacoes.DestinatarioNotificacao>(d =>
                d.RecebeErros && !d.RecebeFalhas && !d.RecebeResumo);
        detalhe.Should().Contain("Enviar ao suporte agora").And.NotContain("Nenhum destinatário ativo recebe erros");
        enviar.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var comentario = $"{login}: acontece ao salvar a empresa 15";
        (await new SqlErroStore(db.Connections, TimeProvider.System).ObterAsync(_id, Ct))!.Should().Match<ErroAplicacao>(e =>
            e.EnvioPedidoPor == login && e.EnvioPedidoEm != null && e.Comentario!.EndsWith(comentario, StringComparison.Ordinal));
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().Contain(AcoesAuditoria.ErroEnvioSolicitado);
    }

    [Fact]
    public async Task Operator_sees_the_screen_only_when_the_admin_allows_and_never_changes_the_situation()
    {
        var admin = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Admin));
        var operador = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Operador));
        var consulta = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        (await operador.GetAsync("/Erros", Ct)).Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await operador.GetStringAsync("/", Ct)).Should().NotContain("/Erros");

        var salvar = await WebHarness.PostFormAsync(admin, "/Notificacoes", "/Notificacoes/SalvarErros", new()
        {
            ["Erros.ExecucaoCaida"] = "false",
            ["Erros.OperadorVe"] = "true",
            ["Erros.RetencaoDias"] = "30",
        });

        salvar.StatusCode.Should().Be(HttpStatusCode.Redirect, await salvar.Content.ReadAsStringAsync(Ct));
        (await new SqlErroStore(db.Connections, TimeProvider.System).ConfiguracaoAsync(Ct)).Should().Match<ConfiguracaoErros>(c =>
            c.OperadorVe && !c.ExecucaoCaida && c.RetencaoDias == 30 && c.ModoEnvio == ModosEnvioErro.Automatico);
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria")).Should().Contain(AcoesAuditoria.ErroConfigAlterada);

        var tela = await operador.GetAsync("/Erros", Ct);
        tela.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tela.Content.ReadAsStringAsync(Ct)).Should().Contain(CodigoErro.De(_id));
        (await WebHarness.PostFormAsync(operador, "/Erros", $"/Erros/Resolver/{_id}", [])).Headers.Location!.ToString()
            .Should().Contain("/Conta/AcessoNegado");
        (await consulta.GetAsync("/Erros", Ct)).Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await new SqlErroStore(db.Connections, TimeProvider.System).ObterAsync(_id, Ct))!.Situacao.Should().Be(SituacoesErro.Aberto);
    }
}
