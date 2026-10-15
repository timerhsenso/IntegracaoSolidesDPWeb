using System.Net;
using IntegracaoSolidesDP.Tests.Sql;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Web.Identity;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.State;

namespace IntegracaoSolidesDP.Tests.Web;

/// <summary>Tela Conferência: lista, detalhe sem o CPF completo, aviso no Painel, "Reenviar do RHSenso" e "Ignorar".</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ConferenciaWebTests(SqlServerFixture db) : IAsyncLifetime
{
    private WebHarness _web = null!;
    private SqlStateStore _state = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _state = new SqlStateStore(db.Connections, TimeProvider.System);
        await _state.EnsureSchemaAsync(Ct);
        await new SqlManagementStore(db.Connections, TimeProvider.System).EnsureSchemaAsync(Ct);
        _web = new WebHarness(db);
    }

    public async ValueTask DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Operator_sees_the_differences_and_asks_to_resend_from_rhsenso()
    {
        var id = await SemearAsync();
        var login = await _web.CriarUsuarioAsync(Perfis.Operador);
        var client = await _web.EntrarAsync(login);

        (await client.GetStringAsync("/Conferencia", Ct)).Should().Contain($"data-conferencia=\"{id}\"");
        (await client.GetStringAsync("/", Ct)).Should().Contain("data-conferencia-divergente=\"15\"");
        var linhas = await client.GetStringAsync($"/Conferencia/Linhas/{id}", Ct);
        linhas.Should().Contain("MARIA SOUZA").And.NotContain(TestData.Cpf1, "a grade mostra só o CPF mascarado");

        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Reenviar/{id}",
            new() { ["rotulos"] = "15-00000001" });

        resposta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await _state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().BeNull("a próxima execução reenvia");
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().Contain(AcoesAuditoria.ReenvioSolicitado);
    }

    [Fact]
    public async Task Who_is_still_to_be_linked_shows_up_marked_and_is_not_resent()
    {
        var agora = DateTimeOffset.UtcNow;
        var id = await new SqlConferenciaStore(db.Connections).SalvarAsync(new Conferencia
        {
            Instancia = "default", Cdempresa = 15, IniciadaEm = agora, ConcluidaEm = agora, Gatilho = GatilhosConferencia.Pedido,
            SolicitadoPor = "carlos", Conferidos = 31, AVincular = 31, Pessoas = 1, Divergencias = 1,
            Linhas =
            [
                new DivergenciaCampo
                {
                    Rotulo = "15-00000002", Nome = "JOAO", Cpf = TestData.Cpf2, CpfMascarado = "111.***.***-35", CadastroDp = 77,
                    Campo = CamposConferencia.Email, RhSenso = "joao@adn.com.br", Dp = "outro@adn.com.br", AVincular = true,
                },
            ],
        }, Ct);
        var login = await _web.CriarUsuarioAsync(Perfis.Operador);
        var client = await _web.EntrarAsync(login);

        var detalhe = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync($"/Conferencia/Detalhe/{id}", Ct));
        var linhas = await client.GetStringAsync($"/Conferencia/Linhas/{id}", Ct);
        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Reenviar/{id}",
            new() { ["rotulos"] = "15-00000002" });

        detalhe.Should().Contain("0 vinculado(s), 31 a vincular pelo CPF");
        linhas.Should().Contain("\"aVincular\":true");
        resposta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().NotContain(AcoesAuditoria.ReenvioSolicitado, "quem está a vincular recebe o cadastro do RHSenso no primeiro envio real");
        (await new SqlConferenciaStore(db.Connections).ObterAsync(id, Ct))!.AVincular.Should().Be(31);
    }

    [Fact]
    public async Task Consulta_profile_sees_but_cannot_resend()
    {
        var id = await SemearAsync();
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var detalhe = await client.GetAsync($"/Conferencia/Detalhe/{id}", Ct);
        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Reenviar/{id}", new() { ["rotulos"] = "15-00000001" });

        detalhe.StatusCode.Should().Be(HttpStatusCode.OK);
        resposta.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await _state.LoadEntityStatesAsync(15, EntityTypes.Employee, Ct))[TestData.Cpf1].PayloadHash.Should().NotBeNull();
    }

    [Fact]
    public async Task Operator_ignores_who_is_only_in_dp_and_brings_them_back_on_the_companies_screen()
    {
        await new RhuSeed(db).FilialAsync(empresa: 15, filial: 1, nome: "ADN PROJETOS");
        await new SqlManagementStore(db.Connections, TimeProvider.System).SeedConfigurationAsync("default", SyncOptionsJson.Serialize(new SyncOptions()), Ct);
        var id = await SemearAsync(comSoNoDp: true);
        var login = await _web.CriarUsuarioAsync(Perfis.Operador);
        var client = await _web.EntrarAsync(login);

        (await client.GetStringAsync($"/Conferencia/Linhas/{id}", Ct)).Should().Contain("\"chave\":\"dp-5538323\",\"soNoDp\":true,\"ignorado\":false");
        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Ignorar/{id}",
            new() { ["cadastros"] = "5538323" });

        resposta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var ignorado = (await db.QueryAsync<(long Id, string Cpf, long Cadastro, string Por)>(
            "SELECT id, cpf, tangerino_id, ignorado_por FROM solidesdp.colaborador_ignorado WHERE removido_em IS NULL")).Should().ContainSingle().Subject;
        ignorado.Should().Be((ignorado.Id, TestData.Cpf3, 5538323L, login));
        (await client.GetStringAsync($"/Conferencia/Linhas/{id}", Ct)).Should().Contain("\"ignorado\":true");
        (await client.GetStringAsync("/Empresas/Editar/15", Ct)).Should().Contain($"data-ignorado=\"{ignorado.Id}\"");

        var voltar = await WebHarness.PostFormAsync(client, "/Empresas/Editar/15", "/Empresas/VoltarAValidar/15",
            new() { ["ignorado"] = ignorado.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        voltar.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await db.QueryAsync<string>("SELECT removido_por FROM solidesdp.colaborador_ignorado")).Should().Equal(login);
        (await db.QueryAsync<string>("SELECT acao FROM solidesdp.auditoria WHERE usuario = @login", new { login }))
            .Should().Contain(AcoesAuditoria.IgnoradoIncluido).And.Contain(AcoesAuditoria.IgnoradoRemovido);
    }

    [Fact]
    public async Task Consulta_profile_cannot_ignore()
    {
        var id = await SemearAsync(comSoNoDp: true);
        var client = await _web.EntrarAsync(await _web.CriarUsuarioAsync(Perfis.Consulta));

        var resposta = await WebHarness.PostFormAsync(client, $"/Conferencia/Detalhe/{id}", $"/Conferencia/Ignorar/{id}", new() { ["cadastros"] = "5538323" });

        resposta.Headers.Location!.ToString().Should().Contain("/Conta/AcessoNegado");
        (await db.QueryAsync<int>("SELECT COUNT(*) FROM solidesdp.colaborador_ignorado")).Should().Equal(0);
    }

    private async Task<long> SemearAsync(bool comSoNoDp = false)
    {
        await _state.UpsertEntityStateAsync(15, new EntityState
        {
            EntityType = EntityTypes.Employee, ExternalId = TestData.Cpf1, RemoteId = 9, PayloadHash = new string('a', 64),
            Status = EntityStatuses.Synced, Matricula = "00000001", Cdfilial = 1,
        }, Ct);
        var linhas = new List<DivergenciaCampo>
        {
            new()
            {
                Rotulo = "15-00000001", Nome = "MARIA SOUZA", Cpf = TestData.Cpf1, CpfMascarado = "123.***.***-09", CadastroDp = 9,
                Campo = CamposConferencia.Nome, RhSenso = "MARIA SOUZA", Dp = "MARIA",
            },
        };
        if (comSoNoDp)
        {
            linhas.Add(new DivergenciaCampo
            {
                Rotulo = string.Empty, Nome = "CLEBER COORDENADOR", Cpf = TestData.Cpf3, CpfMascarado = "390.***.***-05",
                CadastroDp = 5538323, Campo = CamposConferencia.SoNoDp, RhSenso = "CPF não existe no RHSenso", Dp = "ativo · COORDENADOR",
            });
        }

        var agora = DateTimeOffset.UtcNow;
        return await new SqlConferenciaStore(db.Connections).SalvarAsync(new Conferencia
        {
            Instancia = "default", Cdempresa = 15, IniciadaEm = agora, ConcluidaEm = agora, Gatilho = GatilhosConferencia.Agendada,
            Conferidos = 1, Pessoas = linhas.Count, Divergencias = linhas.Count,
            Linhas = linhas,
        }, Ct);
    }
}
