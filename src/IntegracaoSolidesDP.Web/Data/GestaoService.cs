using IntegracaoSolidesDP.Web.Infrastructure;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Data;

/// <summary>Resultado de uma ação da Web: sucesso (com o id gerado) ou as mensagens de erro.</summary>
public sealed record ResultadoGestao(bool Sucesso, long? Id, IReadOnlyList<string> Erros)
{
    public static ResultadoGestao Ok(long id) => new(true, id, []);

    public static ResultadoGestao Falha(params string[] erros) => new(false, null, erros);
}

/// <summary>
/// Ações que alteram a gestão: nova versão da configuração (inclusive ativar/desativar) e pedidos
/// ao serviço. Tudo é INSERT e tudo é auditado.
/// </summary>
public sealed class GestaoService(
    IManagementStore store,
    PainelRepository painel,
    Auditoria auditoria,
    IOptions<WebOptions> options)
{
    private string Instance => options.Value.InstanceName;

    public Task<ConfigurationVersion?> ConfiguracaoAtualAsync(CancellationToken ct) =>
        store.GetCurrentConfigurationAsync(Instance, ct);

    public async Task<ResultadoGestao> SalvarConfiguracaoAsync(SyncOptions regras, string observacao, string usuario, CancellationToken ct)
    {
        regras.InstanceName = Instance;
        var validacao = new SyncOptionsValidator().Validate(null, regras);
        if (validacao.Failed)
        {
            return ResultadoGestao.Falha(validacao.Failures?.ToArray() ?? [validacao.FailureMessage ?? "Configuração inválida."]);
        }

        var atual = await store.GetCurrentConfigurationAsync(Instance, ct);
        var versao = await store.AddConfigurationVersionAsync(
            Instance, atual?.Active ?? true, SyncOptionsJson.Serialize(regras), observacao, usuario, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.ConfiguracaoAlterada, $"versão {versao}: {observacao}", ct: ct);
        return ResultadoGestao.Ok(versao);
    }

    public async Task<ResultadoGestao> DefinirAtivaAsync(bool ativa, string motivo, string usuario, CancellationToken ct)
    {
        var atual = await store.GetCurrentConfigurationAsync(Instance, ct);
        if (atual is null)
        {
            return ResultadoGestao.Falha("Ainda não existe configuração: salve a configuração antes de ativar ou desativar.");
        }

        if (atual.Active == ativa)
        {
            return ResultadoGestao.Falha(ativa ? "A integração já está ativa." : "A integração já está desativada.");
        }

        var versao = await store.AddConfigurationVersionAsync(Instance, ativa, atual.SyncJson, motivo, usuario, ct);
        await auditoria.RegistrarAsync(
            ativa ? AcoesAuditoria.IntegracaoAtivada : AcoesAuditoria.IntegracaoDesativada, $"versão {versao}: {motivo}", ct: ct);
        return ResultadoGestao.Ok(versao);
    }

    public async Task<ResultadoGestao> SolicitarComandoAsync(string tipo, string usuario, CancellationToken ct)
    {
        if (!CommandTypes.All.Contains(tipo, StringComparer.Ordinal))
        {
            return ResultadoGestao.Falha($"Comando desconhecido: {tipo}.");
        }

        if (tipo == CommandTypes.Run && await store.GetCurrentConfigurationAsync(Instance, ct) is { Active: false })
        {
            return ResultadoGestao.Falha("A integração está desativada: só a simulação (dry-run) é permitida.");
        }

        if (await painel.ComandoEmAbertoAsync(tipo, ct))
        {
            return ResultadoGestao.Falha($"Já existe um pedido \"{Rotulos.Comando(tipo)}\" aguardando o serviço.");
        }

        var id = await store.EnqueueCommandAsync(Instance, tipo, usuario, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.ComandoSolicitado, $"{tipo} (pedido {id})", ct: ct);
        return ResultadoGestao.Ok(id);
    }
}
