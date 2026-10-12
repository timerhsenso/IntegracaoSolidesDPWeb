using System.Globalization;
using System.Net;
using System.Text;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Notificacoes;

/// <summary>
/// Avisos por e-mail (só com Gestao:Habilitada e a configuração ativa na tela Notificações):
/// <list type="bullet">
/// <item><b>Falha</b> (execução que falhou ou terminou com erros): na hora, mas só quando a empresa passa de OK para falha;
/// continuando, um lembrete a cada <see cref="ConfiguracaoNotificacao.LembreteHoras"/>; e um aviso quando volta ao normal.</item>
/// <item><b>Pendências</b> (itens bloqueados ou ignorados por dado do RHSenso): resumo diário, no horário configurado,
/// só com as pendências novas desde o último resumo (e o total em aberto).</item>
/// </list>
/// Simulações não avisam, salvo se a configuração pedir. Um erro aqui nunca interrompe a sincronização.
/// </summary>
public sealed class Notificador(
    INotificacaoStore store,
    IEnviadorEmail enviador,
    ITokenProtector tokens,
    IOptions<ManagementOptions> management,
    EpochDates dates,
    TimeProvider clock,
    ILogger<Notificador> logger)
{
    private const int MaximoItensNoEmail = 30;
    private const string Prefixo = "[Integração Sólides DP]";

    /// <summary>Depois de cada execução de uma empresa: pendências, falha e volta ao normal.</summary>
    public async Task AposExecucaoAsync(RunSummary execucao, IReadOnlyList<RunItem> itens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(itens);
        if (!management.Value.Habilitada || execucao.RunId == Guid.Empty)
        {
            return;
        }

        try
        {
            await ProcessarExecucaoAsync(execucao, itens, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao processar os avisos da execução {RunId}", execucao.RunId);
        }
    }

    /// <summary>A cada ciclo do serviço: envia o resumo diário das pendências quando chega a hora (uma vez por dia).</summary>
    public async Task ResumoSeDevidoAsync(CancellationToken ct)
    {
        if (!management.Value.Habilitada)
        {
            return;
        }

        try
        {
            await ProcessarResumoAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao processar o resumo diário das pendências");
        }
    }

    /// <summary>E-mail de teste para todos os destinatários ativos (pedido pela Web). Erro = exceção com o motivo.</summary>
    public async Task<string> EnviarTesteAsync(CancellationToken ct)
    {
        var config = await store.ConfiguracaoAsync(ct)
                     ?? throw new InvalidOperationException("Os avisos por e-mail ainda não foram configurados (tela Notificações).");
        if (!config.Completa)
        {
            throw new InvalidOperationException("Configuração incompleta: informe servidor, porta e remetente.");
        }

        var para = (await store.DestinatariosAsync(ct)).Where(d => d.Ativo).Select(d => d.Email).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (para.Count == 0)
        {
            throw new InvalidOperationException("Nenhum destinatário ativo cadastrado.");
        }

        var html = new StringBuilder()
            .Append("<p>Este é um e-mail de teste da Integração RHSenso → Sólides DP.</p>")
            .Append("<p>Se você recebeu, o servidor de e-mail está configurado corretamente.</p>");
        AppendLink(html, config, "Notificacoes", "Abrir a tela Notificações");
        var erro = await EnviarAsync(config, TiposNotificacao.Teste, null, para, $"{Prefixo} E-mail de teste", html.ToString(), ct);
        if (erro is not null)
        {
            throw new InvalidOperationException($"O e-mail não foi enviado: {erro}");
        }

        return $"E-mail de teste enviado para {string.Join(", ", para)}.";
    }

    private async Task ProcessarExecucaoAsync(RunSummary execucao, IReadOnlyList<RunItem> itens, CancellationToken ct)
    {
        var config = await store.ConfiguracaoAsync(ct);
        if (config is not { Ativo: true } || (execucao.DryRun && !config.NotificarSimulacao))
        {
            return;
        }

        var cdempresa = execucao.Cdempresa ?? 0;
        var agora = clock.GetUtcNow();

        // Execução que parou no meio não viu todo mundo: não resolve pendências.
        if (execucao.Status is RunStatuses.Completed or RunStatuses.CompletedWithPending or RunStatuses.CompletedWithErrors)
        {
            await store.AtualizarPendenciasAsync(cdempresa, Pendencias(itens), agora, ct);
        }

        var emFalha = execucao.Status is RunStatuses.Failed or RunStatuses.CompletedWithErrors;
        var anterior = await store.EstadoAsync(cdempresa, ct) ?? new EstadoNotificacao { Cdempresa = cdempresa };
        if (emFalha)
        {
            var lembrete = TimeSpan.FromHours(Math.Max(1, config.LembreteHoras));
            var tipo = !anterior.EmFalha ? TiposNotificacao.Falha
                : anterior.UltimoAviso is null || agora - anterior.UltimoAviso.Value >= lembrete ? TiposNotificacao.Lembrete
                : null;
            var desde = anterior.EmFalha ? anterior.FalhaDesde ?? agora : agora;
            if (tipo is not null)
            {
                await AvisarFalhaAsync(config, tipo, execucao, itens, desde, ct);
            }

            await store.SalvarEstadoAsync(new EstadoNotificacao
            {
                Cdempresa = cdempresa,
                EmFalha = true,
                FalhaDesde = desde,
                UltimoAviso = tipo is null ? anterior.UltimoAviso : agora,
            }, ct);
        }
        else if (anterior.EmFalha)
        {
            await AvisarNormalizouAsync(config, execucao, anterior.FalhaDesde, ct);
            await store.SalvarEstadoAsync(new EstadoNotificacao { Cdempresa = cdempresa, EmFalha = false, UltimoAviso = agora }, ct);
        }
    }

    private async Task ProcessarResumoAsync(CancellationToken ct)
    {
        var config = await store.ConfiguracaoAsync(ct);
        if (config is not { Ativo: true })
        {
            return;
        }

        var agora = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(agora, dates.Zone);
        if (config.SomenteDiasUteis && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }

        if (local.TimeOfDay < config.HoraResumo)
        {
            return;
        }

        if (config.UltimoResumo is { } ultimo && TimeZoneInfo.ConvertTime(ultimo, dates.Zone).Date == local.Date)
        {
            return;
        }

        var abertas = await store.PendenciasAbertasAsync(ct);
        var destinatarios = (await store.DestinatariosAsync(ct)).Where(d => d.Ativo && d.RecebeResumo).ToList();
        foreach (var destinatario in destinatarios)
        {
            var minhas = abertas.Where(p => destinatario.Atende(p.Cdempresa)).ToList();
            var novas = minhas.Where(p => config.UltimoResumo is null || p.PrimeiraVez > config.UltimoResumo.Value).ToList();
            if (novas.Count == 0)
            {
                continue;
            }

            var assunto = string.Create(CultureInfo.InvariantCulture,
                $"{Prefixo} Pendências: {novas.Count} nova(s), {minhas.Count} em aberto");
            await EnviarAsync(config, TiposNotificacao.Resumo, destinatario.Cdempresa, [destinatario.Email], assunto,
                HtmlResumo(config, novas, minhas), ct);
        }

        // Processado hoje, com ou sem envio: amanhã compara com agora.
        await store.MarcarResumoAsync(agora, ct);
    }

    private async Task AvisarFalhaAsync(
        ConfiguracaoNotificacao config, string tipo, RunSummary execucao, IReadOnlyList<RunItem> itens, DateTimeOffset desde, CancellationToken ct)
    {
        var cdempresa = execucao.Cdempresa ?? 0;
        var para = DestinatariosDeFalha(await store.DestinatariosAsync(ct), cdempresa);
        if (para.Count == 0)
        {
            return;
        }

        var empresa = Empresa(execucao.Cdempresa);
        var assunto = tipo == TiposNotificacao.Falha
            ? $"{Prefixo} {empresa}: problema na sincronização ({Status(execucao.Status)})"
            : $"{Prefixo} {empresa}: continua com problema desde {Local(desde)}";

        var html = new StringBuilder();
        html.Append("<p><b>").Append(Encode(empresa)).Append("</b>: a execução de ").Append(Encode(Local(execucao.StartedAt)))
            .Append(execucao.DryRun ? " (simulação)" : string.Empty)
            .Append(" terminou como <b>").Append(Encode(Status(execucao.Status))).Append("</b>.</p>");
        if (tipo == TiposNotificacao.Lembrete)
        {
            html.Append("<p>O problema continua desde ").Append(Encode(Local(desde))).Append(".</p>");
        }

        if (!string.IsNullOrWhiteSpace(execucao.Error))
        {
            html.Append("<p>Erro:</p><pre style=\"white-space:pre-wrap\">").Append(Encode(Resumir(execucao.Error, 2000))).Append("</pre>");
        }

        var falhas = itens.Where(i => i.Status == ItemStatuses.Failed).ToList();
        if (falhas.Count > 0)
        {
            html.Append("<p>Itens com falha:</p>");
            AppendTabela(html, falhas.Take(MaximoItensNoEmail).Select(i => (Entidade(i.EntityType) + " " + i.ExternalId, i.Message ?? string.Empty)));
            if (falhas.Count > MaximoItensNoEmail)
            {
                html.Append("<p>… e mais ").Append(falhas.Count - MaximoItensNoEmail).Append(".</p>");
            }
        }

        html.Append("<p>Você receberá outro e-mail quando a empresa voltar ao normal.</p>");
        AppendLink(html, config, string.Create(CultureInfo.InvariantCulture, $"Execucoes/Detalhe/{execucao.RunId}"), "Abrir a execução");
        await EnviarAsync(config, tipo, execucao.Cdempresa, para, assunto, html.ToString(), ct);
    }

    private async Task AvisarNormalizouAsync(ConfiguracaoNotificacao config, RunSummary execucao, DateTimeOffset? desde, CancellationToken ct)
    {
        var para = DestinatariosDeFalha(await store.DestinatariosAsync(ct), execucao.Cdempresa ?? 0);
        if (para.Count == 0)
        {
            return;
        }

        var empresa = Empresa(execucao.Cdempresa);
        var html = new StringBuilder();
        html.Append("<p><b>").Append(Encode(empresa)).Append("</b>: a execução de ").Append(Encode(Local(execucao.StartedAt)))
            .Append(" terminou como <b>").Append(Encode(Status(execucao.Status))).Append("</b>.</p>");
        if (desde is { } d)
        {
            html.Append("<p>O problema vinha desde ").Append(Encode(Local(d))).Append(".</p>");
        }

        AppendLink(html, config, string.Create(CultureInfo.InvariantCulture, $"Execucoes/Detalhe/{execucao.RunId}"), "Abrir a execução");
        await EnviarAsync(config, TiposNotificacao.Normalizou, execucao.Cdempresa, para, $"{Prefixo} {empresa}: voltou ao normal", html.ToString(), ct);
    }

    private static string HtmlResumo(ConfiguracaoNotificacao config, IReadOnlyList<PendenciaAberta> novas, IReadOnlyList<PendenciaAberta> abertas)
    {
        var html = new StringBuilder("<p>Pendências de cadastro encontradas pela integração (a integração não envia essas pessoas até o RH resolver).</p>");
        foreach (var empresa in novas.GroupBy(p => p.Cdempresa).OrderBy(g => g.Key))
        {
            var total = abertas.Count(p => p.Cdempresa == empresa.Key);
            html.Append("<h3 style=\"margin-bottom:4px\">").Append(Encode(Empresa(empresa.Key))).Append("</h3>")
                .Append("<p style=\"margin-top:0\">").Append(empresa.Count()).Append(" nova(s), ").Append(total).Append(" em aberto no total.</p>");
            AppendTabela(html, empresa.Take(MaximoItensNoEmail).Select(p => (p.Rotulo, p.Mensagem)));
            if (empresa.Count() > MaximoItensNoEmail)
            {
                html.Append("<p>… e mais ").Append(empresa.Count() - MaximoItensNoEmail).Append(".</p>");
            }
        }

        AppendLink(html, config, "Pendencias", "Abrir Pendências");
        return html.ToString();
    }

    /// <summary>Envia e registra em solidesdp.notificacao_envio. Devolve o erro (null = enviado).</summary>
    private async Task<string?> EnviarAsync(
        ConfiguracaoNotificacao config, string tipo, int? cdempresa, IReadOnlyList<string> para, string assunto, string html, CancellationToken ct)
    {
        string? erro = null;
        try
        {
            await enviador.EnviarAsync(Smtp(config), new MensagemEmail(para, assunto, Corpo(html)), ct);
            logger.LogInformation("Aviso {Tipo} enviado para {Para}: {Assunto}", tipo, string.Join(", ", para), assunto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            erro = ex is System.Security.Cryptography.CryptographicException
                ? "a senha do SMTP não pôde ser lida (cadastrada com outra chave? grave a senha de novo na tela Notificações)"
                : ex.InnerException is { } interna ? $"{ex.Message} ({interna.Message})" : ex.Message;
            logger.LogWarning(ex, "Aviso {Tipo} para {Para} não foi enviado", tipo, string.Join(", ", para));
        }

        await store.RegistrarEnvioAsync(new EnvioNotificacao
        {
            EnviadoEm = clock.GetUtcNow(),
            Tipo = tipo,
            Cdempresa = cdempresa,
            Para = string.Join(", ", para),
            Assunto = assunto,
            Ok = erro is null,
            Erro = erro,
        }, CancellationToken.None);
        return erro;
    }

    private ConfiguracaoSmtp Smtp(ConfiguracaoNotificacao config)
    {
        if (!config.Completa)
        {
            throw new InvalidOperationException("configuração de e-mail incompleta (servidor, porta e remetente)");
        }

        var senha = config.SenhaCifrada is { Length: > 0 } cifrada ? tokens.Unprotect(cifrada) : null;
        return new ConfiguracaoSmtp(config.Servidor!.Trim(), config.Porta, config.Usuario?.Trim(), senha, config.Remetente!.Trim(), config.StartTls);
    }

    private static List<string> DestinatariosDeFalha(IReadOnlyList<DestinatarioNotificacao> destinatarios, int cdempresa) =>
        destinatarios
            .Where(d => d.Ativo && d.RecebeFalhas && d.Atende(cdempresa))
            .Select(d => d.Email)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Pendência = item bloqueado, ou ignorado por dado do RHSenso (CPF inválido ou ativo em duas matrículas). Não entram
    /// "superseded" (linha duplicada, só informativo) nem "fora_do_escopo" (filial não marcada, decisão de configuração).
    /// </summary>
    public static IReadOnlyList<PendenciaAtual> Pendencias(IEnumerable<RunItem> itens) =>
        itens
            .Where(i => i.Status == ItemStatuses.Blocked
                        || (i.Status == ItemStatuses.Skipped && i.Message is { } m
                            && !m.StartsWith("superseded", StringComparison.Ordinal)
                            && !m.StartsWith("fora_do_escopo", StringComparison.Ordinal)))
            .Select(i => new PendenciaAtual($"{i.EntityType}|{i.ExternalId}|{Codigo(i.Message)}", i.ExternalId, i.Message ?? string.Empty))
            .ToList();

    /// <summary>"cpf_desligado_no_dp: ..." → "cpf_desligado_no_dp"; mensagem sem código → o começo dela.</summary>
    private static string Codigo(string? mensagem)
    {
        if (string.IsNullOrEmpty(mensagem))
        {
            return string.Empty;
        }

        var doisPontos = mensagem.IndexOf(':', StringComparison.Ordinal);
        var codigo = doisPontos is > 0 and <= 60 && !mensagem[..doisPontos].Contains(' ', StringComparison.Ordinal)
            ? mensagem[..doisPontos]
            : mensagem;
        return codigo.Length <= 60 ? codigo : codigo[..60];
    }

    private string Local(DateTimeOffset quando) =>
        TimeZoneInfo.ConvertTime(quando, dates.Zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Empresa(int? cdempresa) =>
        cdempresa is int e && e > 0 ? string.Create(CultureInfo.InvariantCulture, $"Empresa {e}") : "Integração";

    private static string Status(string status) => status switch
    {
        RunStatuses.Completed => "Concluída",
        RunStatuses.CompletedWithPending => "Concluída com pendências",
        RunStatuses.CompletedWithErrors => "Concluída com erros",
        RunStatuses.Failed => "Falhou",
        _ => status,
    };

    private static string Entidade(string tipo) => tipo switch
    {
        EntityTypes.Employee => "Colaborador",
        EntityTypes.JobRole => "Cargo",
        EntityTypes.Workplace => "Local",
        EntityTypes.Vacation => "Férias",
        EntityTypes.Company => "Empresa",
        _ => tipo,
    };

    private static string Resumir(string texto, int tamanho) => texto.Length <= tamanho ? texto : texto[..tamanho] + "…";

    private static string Encode(string? texto) => WebUtility.HtmlEncode(texto ?? string.Empty);

    private static void AppendTabela(StringBuilder html, IEnumerable<(string Registro, string Mensagem)> linhas)
    {
        html.Append("<table style=\"border-collapse:collapse;font-size:13px\">");
        foreach (var (registro, mensagem) in linhas)
        {
            html.Append("<tr><td style=\"border:1px solid #ccc;padding:4px 8px;white-space:nowrap;vertical-align:top\">")
                .Append(Encode(registro))
                .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px\">")
                .Append(Encode(mensagem))
                .Append("</td></tr>");
        }

        html.Append("</table>");
    }

    private static void AppendLink(StringBuilder html, ConfiguracaoNotificacao config, string caminho, string texto)
    {
        if (string.IsNullOrWhiteSpace(config.UrlWeb))
        {
            return;
        }

        var url = config.UrlWeb.Trim().TrimEnd('/') + "/" + caminho;
        html.Append("<p><a href=\"").Append(Encode(url)).Append("\">").Append(Encode(texto)).Append("</a></p>");
    }

    private static string Corpo(string conteudo) =>
        "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#222\">" + conteudo +
        "<p style=\"color:#888;font-size:12px\">Aviso automático da Integração RHSenso → Sólides DP. " +
        "Para mudar quem recebe, use a tela Notificações da Web.</p></div>";
}
