using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Textos e cores dos códigos gravados pelo serviço. Os mesmos mapas vão para o JavaScript.</summary>
public static class Rotulos
{
    public sealed record Rotulo(string Texto, string Cor);

    public static readonly IReadOnlyDictionary<string, Rotulo> Status = new Dictionary<string, Rotulo>(StringComparer.Ordinal)
    {
        // Execuções (solidesdp.runs.status)
        ["running"] = new("Em andamento", "info"),
        ["completed"] = new("Concluída", "success"),
        ["completed_with_errors"] = new("Concluída com erros", "warning"),
        ["failed"] = new("Falhou", "danger"),

        // Itens (solidesdp.run_items.status) e estado
        ["created"] = new("Criado", "success"),
        ["updated"] = new("Atualizado", "primary"),
        ["adopted"] = new("Adotado", "info"),
        ["unchanged"] = new("Sem alteração", "secondary"),
        ["dismissed"] = new("Desligado", "dark"),
        ["cancelled"] = new("Cancelado", "dark"),
        ["dry_run"] = new("Simulado", "info"),
        ["skipped"] = new("Ignorado", "secondary"),
        ["deferred"] = new("Adiado", "secondary"),
        ["blocked"] = new("Bloqueado", "warning"),
        ["warning"] = new("Aviso", "warning"),
        ["synced"] = new("Sincronizado", "success"),
        ["pending"] = new("Pendente", "warning"),
        ["failed_permanent"] = new("Desistiu", "danger"),

        // Comandos (solidesdp.comando.status)
        ["pendente"] = new("Aguardando o serviço", "warning"),
        ["executando"] = new("Executando", "info"),
        ["concluido"] = new("Concluído", "success"),
        ["falhou"] = new("Falhou", "danger"),
    };

    public static readonly IReadOnlyDictionary<string, string> Entidades = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["employee"] = "Colaborador",
        ["job_role"] = "Cargo",
        ["workplace"] = "Local de trabalho",
        ["company"] = "Empresa",
        ["vacation"] = "Férias",
    };

    public static readonly IReadOnlyDictionary<string, string> Acoes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["create"] = "Criar",
        ["update"] = "Atualizar",
        ["dismiss"] = "Desligar",
        ["cancel"] = "Cancelar",
        ["resolve"] = "Resolver",
        ["none"] = "—",
    };

    public static readonly IReadOnlyDictionary<string, string> Comandos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EXECUTAR"] = "Executar agora",
        ["EXECUTAR_DRYRUN"] = "Simular (dry-run)",
        ["CHECK_CONFIG"] = "Verificar configuração",
        ["DISCOVER"] = "Consultar o Sólides DP",
        ["RECONCILE"] = "Conferir enviados",
    };

    public static readonly IReadOnlyDictionary<string, string> Origens = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["startup"] = "Início do serviço",
        ["schedule"] = "Agendada",
        ["cli"] = "Linha de comando",
        ["cli-dry-run"] = "Linha de comando (dry-run)",
        ["web"] = "Web",
    };

    /// <summary>Tudo junto, serializado no layout para o site.js.</summary>
    public static object ParaJavaScript => new
    {
        status = Status.ToDictionary(p => p.Key, p => new { texto = p.Value.Texto, cor = p.Value.Cor }),
        entidades = Entidades,
        acoes = Acoes,
        comandos = Comandos,
        origens = Origens,
    };

    public static IHtmlContent Badge(string? status)
    {
        var rotulo = status is not null && Status.TryGetValue(status, out var r) ? r : new Rotulo(status ?? "—", "secondary");
        return new HtmlString($"<span class=\"badge text-bg-{rotulo.Cor}\">{HtmlEncoder.Default.Encode(rotulo.Texto)}</span>");
    }

    public static string Entidade(string? tipo) => tipo is not null && Entidades.TryGetValue(tipo, out var t) ? t : tipo ?? "—";

    public static string Comando(string? tipo) => tipo is not null && Comandos.TryGetValue(tipo, out var t) ? t : tipo ?? "—";

    public static string Origem(string? origem) => origem is not null && Origens.TryGetValue(origem, out var t) ? t : origem ?? "—";
}
