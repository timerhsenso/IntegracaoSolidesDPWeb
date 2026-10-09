using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Options;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Um tópico do manual (Ajuda). O texto fica em <c>Views/Ajuda/Topicos/_{Id}.cshtml</c>.</summary>
/// <param name="Id">Âncora na página Ajuda e nome da partial.</param>
/// <param name="Titulo">Título no sumário.</param>
/// <param name="Icone">Classe do Bootstrap Icons.</param>
/// <param name="Tela">Controller cuja tela abre este tópico no botão "?" (null = tópico geral).</param>
public sealed record TopicoAjuda(string Id, string Titulo, string Icone, string? Tela = null)
{
    public string Partial => $"~/Views/Ajuda/Topicos/_{Id}.cshtml";
}

/// <summary>Sumário do manual, na ordem de leitura. O botão "?" de cada tela abre o tópico dela.</summary>
public static class AjudaTopicos
{
    public static readonly IReadOnlyList<TopicoAjuda> Todos =
    [
        new("visao-geral", "Visão geral", "bi-info-circle"),
        new("acesso", "Acesso e perfis", "bi-person-lock", "Conta"),
        new("implantacao", "Roteiro de implantação", "bi-signpost-split"),
        new("painel", "Painel", "bi-speedometer2", "Painel"),
        new("execucoes", "Execuções", "bi-clock-history", "Execucoes"),
        new("migrados", "O que foi migrado", "bi-people", "Migrados"),
        new("pendencias", "Pendências", "bi-exclamation-triangle", "Pendencias"),
        new("pedidos", "Pedidos ao serviço", "bi-terminal", "Comandos"),
        new("configuracao", "Configuração", "bi-sliders", "Configuracao"),
        new("usuarios", "Usuários", "bi-person-gear", "Usuarios"),
        new("auditoria", "Auditoria", "bi-journal-text", "Auditoria"),
        new("regras", "Como a integração decide", "bi-diagram-3"),
        new("status", "Glossário de status", "bi-tags"),
        new("problemas", "Deu problema, e agora?", "bi-life-preserver"),
        new("ti", "Para o TI", "bi-hdd-network"),
    ];

    /// <summary>Tópico da tela (controller) atual, para o botão "?" do layout.</summary>
    public static TopicoAjuda? DaTela(string? controller) =>
        controller is null ? null : Todos.FirstOrDefault(t => string.Equals(t.Tela, controller, StringComparison.OrdinalIgnoreCase));
}

/// <summary>O que cada status significa e o que fazer. Cobre todos os códigos de <see cref="Rotulos.Status"/> (há teste).</summary>
public static class AjudaStatus
{
    public sealed record Explicacao(string Status, string Significado, string? OQueFazer = null);

    public sealed record Grupo(string Titulo, string Descricao, IReadOnlyList<Explicacao> Itens);

    public static readonly IReadOnlyList<Grupo> Grupos =
    [
        new("Execução", "Status de cada sincronização, em Execuções e no Painel.",
        [
            new("running", "O serviço está sincronizando agora.", "Aguarde. Se ficar assim depois de o serviço parar, a próxima execução marca como Falhou."),
            new("completed", "Terminou e todos os itens foram tratados sem falha nem bloqueio."),
            new("completed_with_errors", "Terminou, mas algum item falhou ou ficou bloqueado. O restante foi enviado normalmente.", "Veja Pendências."),
            new("failed", "A execução parou antes do fim: banco fora do ar, token inválido, configuração inválida ou serviço interrompido.", "Abra a execução e leia o erro no topo."),
        ]),
        new("Item de uma execução", "O que aconteceu com cada registro (colaborador, cargo, local, férias) em uma execução.",
        [
            new("created", "Criado no Sólides DP nesta execução."),
            new("updated", "Já existia no Sólides DP e foi atualizado porque algo mudou no RHSenso."),
            new("adopted", "Já existia no Sólides DP (cadastrado antes, por outra via) e foi vinculado à integração, sem duplicar."),
            new("unchanged", "Nada mudou desde o último envio, então nada foi enviado. Aparece só na contagem da execução."),
            new("dismissed", "Desligado no Sólides DP (demissão ou transferência para outra empresa)."),
            new("cancelled", "Lançamento de férias excluído no Sólides DP (férias reprogramadas ou apagadas no RHSenso)."),
            new("dry_run", "Simulação: mostra o que seria feito, sem enviar nada."),
            new("skipped", "Ficou de fora de propósito: CPF repetido, dado obrigatório faltando, linha duplicada, férias Programadas ou um envio que já desistiu.", "Leia a mensagem. Em geral, corrija o cadastro no RHSenso."),
            new("deferred", "Fica para depois: desligamento com data futura ou férias de quem ainda não está no Sólides DP.", "Nada a fazer: é enviado sozinho quando chegar a hora."),
            new("blocked", "Não pôde ser enviado porque falta algo de que ele depende: empresa (CNPJ) não cadastrada no DP, cargo ou local indisponível.", "Cadastre o que falta (no DP ou no RHSenso). A próxima execução tenta de novo."),
            new("warning", "Enviado ou mantido, mas com algo para conferir. Exemplo: a descrição de um cargo mudou e precisa ser ajustada à mão no DP.", "Leia a mensagem e ajuste no Sólides DP se for o caso."),
            new("failed", "O Sólides DP recusou o envio ou não respondeu. Também aparece quando uma trava de segurança (máximo por execução) segura a etapa inteira: o registro fica \"-\".", "Leia a mensagem e o código HTTP. A próxima execução tenta de novo."),
        ]),
        new("Situação do registro no Sólides DP", "Como cada registro está hoje, em O que foi migrado.",
        [
            new("synced", "Está no Sólides DP e em dia com o RHSenso."),
            new("dismissed", "Colaborador desligado no Sólides DP pela integração."),
            new("pending", "Férias com envio iniciado e sem confirmação. A próxima execução confere no Sólides DP antes de reenviar, para não duplicar."),
            new("failed", "O último envio falhou. A próxima execução tenta de novo."),
            new("failed_permanent", "Férias que falharam o número máximo de vezes. A integração só tenta de novo quando o período mudar no RHSenso.", "Leia o último erro, corrija a causa e altere o período (ou fale com o TI)."),
            new("cancelled", "Lançamento de férias excluído no Sólides DP."),
        ]),
        new("Pedido ao serviço", "Status dos pedidos feitos em Pedidos ao serviço.",
        [
            new("pendente", "Registrado. O serviço atende em alguns segundos.", "Se demorar minutos, o serviço está parado ou com a gestão desligada: fale com o TI."),
            new("executando", "O serviço está atendendo o pedido."),
            new("concluido", "Atendido. O resultado está no detalhe do pedido."),
            new("falhou", "O serviço tentou e não conseguiu.", "Leia o resultado no detalhe do pedido."),
        ]),
    ];
}

/// <summary>
/// Campos da tela Configuração, lidos dos atributos de <see cref="ConfiguracaoForm"/>: nome, dica curta
/// (<see cref="DisplayAttribute.Description"/>) e valor padrão. A tela e o manual usam a mesma fonte.
/// </summary>
public static class CamposConfiguracao
{
    public sealed record Campo(string Propriedade, string Nome, string Dica, string Padrao)
    {
        public string Ancora => $"cfg-{Propriedade}";
    }

    /// <summary>Campos de regra (sem o "Motivo da alteração", que é da versão e não da regra).</summary>
    public static readonly IReadOnlyList<Campo> Todos = Montar();

    public static Campo Obter(string propriedade) =>
        Todos.FirstOrDefault(c => c.Propriedade == propriedade)
        ?? throw new ArgumentException($"Campo de configuração desconhecido: {propriedade}", nameof(propriedade));

    public static string Ancora(string propriedade) => $"cfg-{propriedade}";

    private static List<Campo> Montar()
    {
        var padrao = new ConfiguracaoForm();
        return typeof(ConfiguracaoForm)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(ConfiguracaoForm.Observacao))
            .Select(p =>
            {
                var display = p.GetCustomAttribute<DisplayAttribute>();
                return new Campo(p.Name, display?.Name ?? p.Name, display?.Description ?? string.Empty, Formatar(p.GetValue(padrao)));
            })
            .ToList();
    }

    private static string Formatar(object? valor) => valor switch
    {
        null => "vazio",
        bool b => b ? "ligado" : "desligado",
        string s when string.IsNullOrWhiteSpace(s) => "vazio",
        string s => s,
        CompanyMode.ResolveByCnpj => "procurar a empresa pelo CNPJ da filial",
        CompanyMode.None => "não enviar empresa",
        FeriasEndDateMode.InicioDoDiaSeguinte => "00:00 do dia seguinte ao último dia",
        FeriasEndDateMode.InicioDoUltimoDia => "00:00 do último dia",
        FeriasEndDateMode.FimDoUltimoDia => "23:59:59 do último dia",
        IFormattable f => f.ToString(null, CultureInfo.GetCultureInfo("pt-BR")),
        _ => valor.ToString() ?? string.Empty,
    };
}
