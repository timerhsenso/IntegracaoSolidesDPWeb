using System.Globalization;
using System.Net;
using System.Text;
using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Notificacoes;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// E-mails dos erros da aplicação ao suporte (destinatários com "Erros da aplicação" marcado), a cada ciclo do serviço.
/// Só com Gestao:Habilitada e os avisos ligados em Notificações. Conforme <see cref="ConfiguracaoErros.ModoEnvio"/>:
/// <list type="bullet">
/// <item><b>Desligado</b>: nada sai (os erros continuam registrados).</item>
/// <item><b>Só manual</b>: sai só o que foi pedido no botão "Enviar ao suporte".</item>
/// <item><b>Automático</b>: o pedido no botão, o primeiro aviso de cada erro aberto e, para o erro que continua
/// acontecendo, um resumo a cada <see cref="ConfiguracaoErros.ResumoHoras"/>.</item>
/// </list>
/// O automático respeita a origem (Web, serviço) e o limite diário: ao atingir, sai um único aviso "limite atingido" e o
/// resto espera o dia seguinte. Com o SMTP recusando, tenta de novo em 15 minutos. Nunca lança.
/// </summary>
public sealed class AvisoErros(
    IErroStore erros,
    INotificacaoStore notificacoes,
    Notificador notificador,
    IOptions<ManagementOptions> management,
    EpochDates dates,
    TimeProvider clock,
    ILogger<AvisoErros> logger)
{
    /// <summary>Só erros que aconteceram nas últimas horas geram aviso automático (ligar o envio não manda o histórico).</summary>
    public static readonly TimeSpan JanelaRecentes = TimeSpan.FromHours(24);

    /// <summary>Espera depois de o SMTP recusar.</summary>
    public static readonly TimeSpan PausaAposFalha = TimeSpan.FromMinutes(15);

    private const int TamanhoDetalheNoEmail = 8000;

    private DateTimeOffset _pausaAte = DateTimeOffset.MinValue;

    public async Task ProcessarAsync(CancellationToken ct)
    {
        if (!management.Value.Habilitada || clock.GetUtcNow() < _pausaAte)
        {
            return;
        }

        try
        {
            if (!await EnviarAsync(ct))
            {
                _pausaAte = clock.GetUtcNow() + PausaAposFalha;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao processar os e-mails dos erros da aplicação");
            _pausaAte = clock.GetUtcNow() + PausaAposFalha;
        }
    }

    /// <summary>False quando um envio falhou (pausa antes de tentar de novo).</summary>
    private async Task<bool> EnviarAsync(CancellationToken ct)
    {
        var regras = await erros.ConfiguracaoAsync(ct);
        if (regras.ModoEnvio == ModosEnvioErro.Desligado)
        {
            return true;
        }

        var smtp = await notificacoes.ConfiguracaoAsync(ct);
        if (smtp is not { Ativo: true, Completa: true })
        {
            return true;
        }

        var agora = clock.GetUtcNow();
        var candidatos = await erros.ParaAvisarAsync(agora - JanelaRecentes, ct);
        if (candidatos.Count == 0)
        {
            return true;
        }

        var destinatarios = (await notificacoes.DestinatariosAsync(ct)).Where(d => d.Ativo && d.RecebeErros).ToList();
        if (destinatarios.Count == 0)
        {
            return true;
        }

        var envios = await erros.EnviosDesdeAsync(InicioDoDia(agora), ct);
        var enviados = envios.Enviados;

        // O que foi pedido no botão sai sempre (é uma pessoa pedindo), mas conta no limite.
        foreach (var pedido in candidatos.Where(e => e.EnvioPedidoEm is not null))
        {
            var resultado = await EnviarErroAsync(smtp, regras, destinatarios, pedido, TiposNotificacao.ErroManual, ct);
            if (resultado == Resultado.Falhou)
            {
                return false;
            }

            enviados += resultado == Resultado.Enviado ? 1 : 0;
        }

        if (regras.ModoEnvio != ModosEnvioErro.Automatico)
        {
            return true;
        }

        var automaticos = candidatos
            .Where(e => e.EnvioPedidoEm is null && e.Situacao == SituacoesErro.Aberto && OrigemLigada(regras, e.Origem))
            .ToList();
        var intervalo = TimeSpan.FromHours(Math.Max(1, regras.ResumoHoras));
        var repetidos = automaticos
            .Where(e => e.UltimoAviso is { } aviso && agora - aviso >= intervalo && e.Ocorrencias > e.OcorrenciasNoAviso)
            .ToList();
        var pendentes = automaticos.Count(e => e.UltimoAviso is null) + repetidos.Count;
        foreach (var novo in automaticos.Where(e => e.UltimoAviso is null))
        {
            if (enviados >= regras.LimiteDia)
            {
                return await AvisarLimiteAsync(smtp, regras, destinatarios, envios.LimiteAvisado, pendentes, ct);
            }

            var resultado = await EnviarErroAsync(smtp, regras, destinatarios, novo, TiposNotificacao.Erro, ct);
            if (resultado == Resultado.Falhou)
            {
                return false;
            }

            enviados += resultado == Resultado.Enviado ? 1 : 0;
            pendentes--;
        }

        foreach (var (para, lista) in PorDestinatario(destinatarios, repetidos))
        {
            if (enviados >= regras.LimiteDia)
            {
                return await AvisarLimiteAsync(smtp, regras, destinatarios, envios.LimiteAvisado, pendentes, ct);
            }

            if (!await EnviarResumoAsync(smtp, regras, para, lista, ct))
            {
                return false;
            }

            enviados++;
        }

        return true;
    }

    private enum Resultado
    {
        Enviado,

        /// <summary>Ninguém recebe erro desta empresa: conta como avisado, para não ficar tentando a cada ciclo.</summary>
        SemDestinatario,

        Falhou,
    }

    private async Task<Resultado> EnviarErroAsync(
        ConfiguracaoNotificacao smtp, ConfiguracaoErros regras, IReadOnlyList<DestinatarioNotificacao> destinatarios, ErroAplicacao erro,
        string tipo, CancellationToken ct)
    {
        var para = Para(destinatarios, erro);
        if (para.Count == 0)
        {
            await erros.MarcarAvisadosAsync([(erro.Id, erro.Ocorrencias)], clock.GetUtcNow(), ct);
            return Resultado.SemDestinatario;
        }

        var html = new StringBuilder();
        if (tipo == TiposNotificacao.ErroManual)
        {
            html.Append("<p><b>").Append(Encode(erro.EnvioPedidoPor ?? "Um usuário")).Append("</b> pediu o envio deste erro ao suporte.</p>");
        }
        else if (erro.UltimoAviso is null && erro.Ocorrencias > 1)
        {
            html.Append("<p>Este erro já aconteceu ").Append(Numero(erro.Ocorrencias)).Append(" vezes.</p>");
        }

        AppendErro(html, erro);
        AppendLink(html, smtp, string.Create(CultureInfo.InvariantCulture, $"Erros/Detalhe/{erro.Id}"), "Abrir o erro");
        var assunto = $"{Prefixo(regras)} Erro {erro.Codigo} {(erro.Origem == OrigensErro.Web ? "na Web" : "no serviço")}: {Resumo(erro.Mensagem, 90)}";
        if (await notificador.EnviarAsync(smtp, tipo, erro.Cdempresa, para, assunto, html.ToString(), ct) is not null)
        {
            return Resultado.Falhou;
        }

        await erros.MarcarAvisadosAsync([(erro.Id, erro.Ocorrencias)], clock.GetUtcNow(), ct);
        return Resultado.Enviado;
    }

    private async Task<bool> EnviarResumoAsync(
        ConfiguracaoNotificacao smtp, ConfiguracaoErros regras, IReadOnlyList<string> para, IReadOnlyList<ErroAplicacao> lista, CancellationToken ct)
    {
        var html = new StringBuilder();
        html.Append("<p>").Append(lista.Count == 1 ? "Um erro já avisado continua acontecendo" : $"{Numero(lista.Count)} erros já avisados continuam acontecendo")
            .Append(" (ocorrências novas desde o último e-mail).</p>");
        html.Append("<table style=\"border-collapse:collapse;font-size:13px\">")
            .Append("<tr><th style=\"border:1px solid #ccc;padding:4px 8px\">Erro</th><th style=\"border:1px solid #ccc;padding:4px 8px\">Novas</th>")
            .Append("<th style=\"border:1px solid #ccc;padding:4px 8px\">Total</th><th style=\"border:1px solid #ccc;padding:4px 8px\">Última vez</th>")
            .Append("<th style=\"border:1px solid #ccc;padding:4px 8px\">Mensagem</th></tr>");
        foreach (var erro in lista)
        {
            html.Append("<tr><td style=\"border:1px solid #ccc;padding:4px 8px;white-space:nowrap\">").Append(Encode(erro.Codigo))
                .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px\">").Append(Numero(erro.Ocorrencias - erro.OcorrenciasNoAviso))
                .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px\">").Append(Numero(erro.Ocorrencias))
                .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px;white-space:nowrap\">").Append(Encode(Local(erro.UltimaVez)))
                .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px\">").Append(Encode(Resumo(erro.Mensagem, 300)))
                .Append("</td></tr>");
        }

        html.Append("</table>")
            .Append("<p>Erro conhecido que não precisa de aviso: use \"Silenciar\" na tela Erros da aplicação.</p>");
        AppendLink(html, smtp, "Erros?situacao=aberto", "Abrir os erros abertos");
        var assunto = $"{Prefixo(regras)} Resumo: {(lista.Count == 1 ? $"o erro {lista[0].Codigo} continua" : $"{Numero(lista.Count)} erros continuam")} acontecendo";
        if (await notificador.EnviarAsync(smtp, TiposNotificacao.ErroResumo, null, para, assunto, html.ToString(), ct) is not null)
        {
            return false;
        }

        await erros.MarcarAvisadosAsync(lista.Select(e => (e.Id, e.Ocorrencias)).ToList(), clock.GetUtcNow(), ct);
        return true;
    }

    /// <summary>Um único aviso por dia; o resto espera o dia seguinte. True = nada falhou.</summary>
    private async Task<bool> AvisarLimiteAsync(
        ConfiguracaoNotificacao smtp, ConfiguracaoErros regras, IReadOnlyList<DestinatarioNotificacao> destinatarios, bool jaAvisado,
        int pendentes, CancellationToken ct)
    {
        if (jaAvisado)
        {
            return true;
        }

        var para = destinatarios.Select(d => d.Email).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var html = new StringBuilder()
            .Append("<p>Hoje já saíram ").Append(Numero(regras.LimiteDia))
            .Append(" e-mail(s) de erro, o limite diário configurado. Os próximos avisos ficam para amanhã")
            .Append(pendentes > 0 ? $" ({Numero(pendentes)} erro(s) aguardando)" : string.Empty).Append(".</p>")
            .Append("<p>Todos os erros continuam registrados na tela Erros da aplicação. O limite muda em Notificações › Erros da aplicação.</p>");
        AppendLink(html, smtp, "Erros?situacao=aberto", "Abrir os erros abertos");
        return await notificador.EnviarAsync(
            smtp, TiposNotificacao.ErroLimite, null, para, $"{Prefixo(regras)} Limite diário de e-mails de erro atingido", html.ToString(), ct) is null;
    }

    private void AppendErro(StringBuilder html, ErroAplicacao erro)
    {
        html.Append("<p style=\"font-size:15px\"><b>").Append(Encode(erro.Codigo)).Append("</b> — ").Append(Encode(erro.Mensagem)).Append("</p>");
        html.Append("<table style=\"border-collapse:collapse;font-size:13px\">");
        Linha(html, "Origem", (erro.Origem == OrigensErro.Web ? "Web" : "Serviço") + (erro.ExecucaoCaida ? " (execução que caiu)" : string.Empty));
        Linha(html, "Ocorrências", Numero(erro.Ocorrencias));
        Linha(html, "Primeira / última vez", $"{Local(erro.PrimeiraVez)} / {Local(erro.UltimaVez)}");
        Linha(html, "Tipo", erro.Tipo);
        Linha(html, "Onde", erro.Fonte);
        Linha(html, "Tela", erro.Tela);
        Linha(html, "Usuário", erro.Usuario);
        Linha(html, "Empresa", erro.Cdempresa?.ToString(CultureInfo.InvariantCulture));
        Linha(html, "Máquina / versão", $"{erro.Maquina ?? "—"} / {erro.Versao ?? "—"}");
        html.Append("</table>");
        if (!string.IsNullOrWhiteSpace(erro.Comentario))
        {
            html.Append("<p><b>Comentários</b></p><pre style=\"white-space:pre-wrap\">").Append(Encode(erro.Comentario)).Append("</pre>");
        }

        if (!string.IsNullOrWhiteSpace(erro.Detalhe))
        {
            html.Append("<p><b>Detalhe técnico</b></p><pre style=\"white-space:pre-wrap;font-size:12px\">")
                .Append(Encode(erro.Detalhe.Length <= TamanhoDetalheNoEmail ? erro.Detalhe : string.Concat(erro.Detalhe.AsSpan(0, TamanhoDetalheNoEmail), "…")))
                .Append("</pre>");
        }
    }

    private static void Linha(StringBuilder html, string rotulo, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return;
        }

        html.Append("<tr><td style=\"border:1px solid #ccc;padding:4px 8px;white-space:nowrap;vertical-align:top\">").Append(Encode(rotulo))
            .Append("</td><td style=\"border:1px solid #ccc;padding:4px 8px\">").Append(Encode(valor)).Append("</td></tr>");
    }

    /// <summary>Destinatário de uma empresa só recebe erro dessa empresa ou sem empresa.</summary>
    private static List<string> Para(IEnumerable<DestinatarioNotificacao> destinatarios, ErroAplicacao erro) =>
        destinatarios
            .Where(d => erro.Cdempresa is not { } empresa || d.Atende(empresa))
            .Select(d => d.Email)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Um resumo por destinatário, só com os erros que ele recebe.</summary>
    private static List<(IReadOnlyList<string> Para, IReadOnlyList<ErroAplicacao> Erros)> PorDestinatario(
        IReadOnlyList<DestinatarioNotificacao> destinatarios, IReadOnlyList<ErroAplicacao> lista)
    {
        var resultado = new List<(IReadOnlyList<string> Para, IReadOnlyList<ErroAplicacao> Erros)>();
        if (lista.Count == 0)
        {
            return resultado;
        }

        var porEmail = destinatarios
            .GroupBy(d => d.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Email: g.First().Email,
                Erros: lista.Where(e => e.Cdempresa is not { } empresa || g.Any(d => d.Atende(empresa))).ToList()))
            .Where(p => p.Erros.Count > 0);

        // Quem recebe os mesmos erros vai junto no mesmo e-mail.
        foreach (var grupo in porEmail.GroupBy(p => string.Join(',', p.Erros.Select(e => e.Id)), StringComparer.Ordinal))
        {
            resultado.Add((grupo.Select(p => p.Email).ToList(), grupo.First().Erros));
        }

        return resultado;
    }

    private static bool OrigemLigada(ConfiguracaoErros regras, string origem) =>
        origem == OrigensErro.Web ? regras.OrigemWeb : regras.OrigemServico;

    private static string Prefixo(ConfiguracaoErros regras) =>
        string.IsNullOrWhiteSpace(regras.NomeCliente) ? "[Integração Sólides DP]" : $"[Integração Sólides DP · {regras.NomeCliente.Trim()}]";

    private DateTimeOffset InicioDoDia(DateTimeOffset agora)
    {
        var local = TimeZoneInfo.ConvertTime(agora, dates.Zone);
        return new DateTimeOffset(local.Date, local.Offset);
    }

    private string Local(DateTimeOffset quando) =>
        TimeZoneInfo.ConvertTime(quando, dates.Zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static void AppendLink(StringBuilder html, ConfiguracaoNotificacao smtp, string caminho, string texto)
    {
        if (string.IsNullOrWhiteSpace(smtp.UrlWeb))
        {
            return;
        }

        var url = smtp.UrlWeb.Trim().TrimEnd('/') + "/" + caminho;
        html.Append("<p><a href=\"").Append(Encode(url)).Append("\">").Append(Encode(texto)).Append("</a></p>");
    }

    private static string Resumo(string texto, int tamanho)
    {
        var linha = texto.ReplaceLineEndings(" ").Trim();
        return linha.Length <= tamanho ? linha : string.Concat(linha.AsSpan(0, tamanho - 1), "…");
    }

    private static string Numero(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    private static string Encode(string? texto) => WebUtility.HtmlEncode(texto ?? string.Empty);
}
