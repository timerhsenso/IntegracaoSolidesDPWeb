using System.Globalization;
using System.Security.Cryptography;
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
    ITokenProtector tokens,
    IOptions<WebOptions> options)
{
    private const int TamanhoMaximoToken = 1000;

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

    /// <summary>
    /// Grava a configuração de uma empresa (habilitada, simulação, filiais...) numa versão nova. As demais empresas
    /// e a seção Sync seguem iguais à versão vigente.
    /// </summary>
    public async Task<ResultadoGestao> SalvarEmpresaAsync(EmpresaConfiguracao empresa, string motivo, string usuario, CancellationToken ct)
    {
        var atual = await store.GetCurrentConfigurationAsync(Instance, ct);
        if (atual is null)
        {
            return ResultadoGestao.Falha("Ainda não existe configuração: rode o serviço uma vez (ele cria a versão 1) ou salve a tela Configuração.");
        }

        SyncOptions geral;
        try
        {
            geral = SyncOptionsJson.Deserialize(atual.SyncJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return ResultadoGestao.Falha($"A versão {atual.Version} da configuração não pôde ser lida; salve a tela Configuração primeiro.");
        }

        // A empresa vai em envio real só com go-live (dela ou o geral), como no serviço.
        var efetiva = EmpresaOptions.Mesclar(geral, empresa);
        if (empresa.Habilitada && !efetiva.DryRun && efetiva.GoLiveDate is null)
        {
            return ResultadoGestao.Falha("Para o envio real, informe a data de go-live da empresa (ou a geral, na tela Configuração).");
        }

        var empresas = atual.Empresas.Where(e => e.Cdempresa != empresa.Cdempresa).Append(empresa).OrderBy(e => e.Cdempresa).ToList();
        var versao = await store.AddConfigurationVersionAsync(Instance, atual.Active, atual.SyncJson, empresas, motivo, usuario, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.EmpresaAlterada, $"empresa {empresa.Cdempresa}, versão {versao}: {Descrever(empresa)}. {motivo}", ct: ct);
        return ResultadoGestao.Ok(versao);
    }

    /// <summary>Cifra e grava o token do Sólides DP da empresa. O token nunca é gravado em texto nem na auditoria.</summary>
    public async Task<ResultadoGestao> SalvarTokenAsync(int cdempresa, string? token, string usuario, CancellationToken ct)
    {
        var limpo = Worker.Api.SolidesDpToken.Normalize(token);
        if (limpo is null)
        {
            return ResultadoGestao.Falha("Cole o token gerado no Sólides DP (Empregador › Integrações).");
        }

        if (limpo.Length > TamanhoMaximoToken || limpo.Any(char.IsWhiteSpace))
        {
            return ResultadoGestao.Falha("O texto colado não parece um token do Sólides DP (tem espaços ou é grande demais).");
        }

        byte[] cifrado;
        try
        {
            cifrado = tokens.Protect(limpo);
        }
        catch (CryptographicException ex)
        {
            return ResultadoGestao.Falha($"Não foi possível cifrar o token: {ex.Message}");
        }

        await store.AddEmpresaTokenAsync(cdempresa, cifrado, usuario, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.EmpresaTokenCadastrado, string.Create(CultureInfo.InvariantCulture, $"empresa {cdempresa}"), ct: ct);
        return ResultadoGestao.Ok(cdempresa);
    }

    public async Task<ResultadoGestao> RemoverTokenAsync(int cdempresa, string motivo, string usuario, CancellationToken ct)
    {
        await store.AddEmpresaTokenAsync(cdempresa, null, usuario, ct);
        await auditoria.RegistrarAsync(AcoesAuditoria.EmpresaTokenRemovido, string.Create(CultureInfo.InvariantCulture, $"empresa {cdempresa}: {motivo}"), ct: ct);
        return ResultadoGestao.Ok(cdempresa);
    }

    public Task<IReadOnlyDictionary<int, EmpresaToken>> TokensAsync(CancellationToken ct) => store.GetEmpresaTokensAsync(ct);

    public Task<ResultadoGestao> SolicitarComandoAsync(string tipo, string usuario, CancellationToken ct) =>
        SolicitarComandoAsync(tipo, usuario, cdempresa: null, ct);

    /// <summary>Pedido ao serviço, para uma empresa ou (<paramref name="cdempresa"/> nulo) para todas as habilitadas.</summary>
    public async Task<ResultadoGestao> SolicitarComandoAsync(string tipo, string usuario, int? cdempresa, CancellationToken ct)
    {
        if (!CommandTypes.All.Contains(tipo, StringComparer.Ordinal))
        {
            return ResultadoGestao.Falha($"Comando desconhecido: {tipo}.");
        }

        if (tipo == CommandTypes.Run && await store.GetCurrentConfigurationAsync(Instance, ct) is { Active: false })
        {
            return ResultadoGestao.Falha("A integração está desativada: só a simulação (dry-run) é permitida.");
        }

        if (await painel.ComandoEmAbertoAsync(tipo, cdempresa, ct))
        {
            return ResultadoGestao.Falha($"Já existe um pedido \"{Rotulos.Comando(tipo)}\" aguardando o serviço{(cdempresa is null ? "" : $" para a empresa {cdempresa}")}.");
        }

        var id = await store.EnqueueCommandAsync(Instance, tipo, usuario, cdempresa, ct);
        var alvo = cdempresa is { } e ? string.Create(CultureInfo.InvariantCulture, $"empresa {e}") : "todas as empresas";
        await auditoria.RegistrarAsync(AcoesAuditoria.ComandoSolicitado, $"{tipo}, {alvo} (pedido {id})", ct: ct);
        return ResultadoGestao.Ok(id);
    }

    private static string Descrever(EmpresaConfiguracao e)
    {
        var partes = new List<string>
        {
            e.Habilitada ? "habilitada" : "desabilitada",
            e.DryRun ? "simulação" : "envio real",
            e.Filiais.Count == 0 ? "todas as filiais" : $"filiais {string.Join(", ", e.Filiais.Order())}",
        };
        if (e.GoLiveDate is { } goLive)
        {
            partes.Add($"go-live {goLive:dd/MM/yyyy}");
        }

        return string.Join("; ", partes);
    }
}
