using System.ComponentModel.DataAnnotations;
using IntegracaoSolidesDP.Web.Data;
using IntegracaoSolidesDP.Worker.Erros;
using IntegracaoSolidesDP.Worker.Notificacoes;

namespace IntegracaoSolidesDP.Web.Models;

/// <summary>Tela Notificações: servidor de e-mail, quando avisar, quem recebe e os últimos envios.</summary>
public sealed class NotificacoesViewModel
{
    public required NotificacaoForm Form { get; init; }

    /// <summary>Já existe senha gravada (nunca é exibida).</summary>
    public bool TemSenha { get; init; }

    public ConfiguracaoNotificacao? Atual { get; init; }

    public IReadOnlyList<DestinatarioNotificacao> Destinatarios { get; init; } = [];

    public IReadOnlyList<EnvioNotificacao> Envios { get; init; } = [];

    public IReadOnlyList<EmpresaRhsenso> Empresas { get; init; } = [];

    public DestinatarioForm NovoDestinatario { get; init; } = new();

    /// <summary>Card "Erros da aplicação".</summary>
    public ErrosConfigForm Erros { get; init; } = new();

    /// <summary>A configuração dos erros gravada (padrões quando nunca foi gravada).</summary>
    public ConfiguracaoErros ErrosAtual { get; init; } = new();
}

public sealed class NotificacaoForm
{
    [Display(Name = "Avisos ligados", Description = "Desligado, nada é enviado. O e-mail de teste funciona mesmo desligado.")]
    public bool Ativo { get; set; }

    [Required(ErrorMessage = "Informe o servidor SMTP.")]
    [StringLength(200, ErrorMessage = "Use até 200 caracteres.")]
    [Display(Name = "Servidor SMTP", Description = "Ex.: smtp.skymail.net.br")]
    public string? Servidor { get; set; }

    [Range(1, 65535, ErrorMessage = "Porta inválida.")]
    [Display(Name = "Porta", Description = "587 com STARTTLS é o padrão para envio autenticado.")]
    public int Porta { get; set; } = 587;

    [StringLength(200, ErrorMessage = "Use até 200 caracteres.")]
    [Display(Name = "Usuário", Description = "Vazio = servidor sem autenticação.")]
    public string? Usuario { get; set; }

    [StringLength(200, ErrorMessage = "Use até 200 caracteres.")]
    [DataType(DataType.Password)]
    [Display(Name = "Senha", Description = "Gravada cifrada e nunca exibida. Deixe em branco para manter a atual.")]
    public string? Senha { get; set; }

    [Display(Name = "Apagar a senha gravada")]
    public bool ApagarSenha { get; set; }

    [Required(ErrorMessage = "Informe o remetente.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    [StringLength(200, ErrorMessage = "Use até 200 caracteres.")]
    [Display(Name = "Remetente", Description = "Endereço que aparece como remetente. Muitos servidores exigem que seja o mesmo do usuário.")]
    public string? Remetente { get; set; }

    [Display(Name = "Usar STARTTLS (criptografia)", Description = "Ligado na porta 587. Porta 465 (SSL direto) não é suportada: use 587.")]
    public bool StartTls { get; set; } = true;

    [Required(ErrorMessage = "Informe a hora do resumo.")]
    [Display(Name = "Hora do resumo diário", Description = "Hora local em que sai o resumo das pendências novas.")]
    public TimeSpan HoraResumo { get; set; } = new(8, 0, 0);

    [Display(Name = "Resumo só em dias úteis", Description = "Sem resumo no sábado e no domingo.")]
    public bool SomenteDiasUteis { get; set; } = true;

    [Range(1, 168, ErrorMessage = "Informe de 1 a 168 horas.")]
    [Display(Name = "Lembrete de falha a cada (horas)", Description = "Com a falha continuando, um novo aviso depois deste tempo.")]
    public int LembreteHoras { get; set; } = 24;

    [Display(Name = "Avisar também nas simulações", Description = "Desligado, só as execuções reais avisam. Útil ligar durante a homologação.")]
    public bool NotificarSimulacao { get; set; }

    [Url(ErrorMessage = "Endereço inválido (comece com https://).")]
    [StringLength(400, ErrorMessage = "Use até 400 caracteres.")]
    [Display(Name = "Endereço desta Web", Description = "Ex.: https://srvhomologacao:8443. Usado nos links dos e-mails; vazio = sem link.")]
    public string? UrlWeb { get; set; }

    public static NotificacaoForm De(ConfiguracaoNotificacao? c) => c is null ? new NotificacaoForm() : new NotificacaoForm
    {
        Ativo = c.Ativo,
        Servidor = c.Servidor,
        Porta = c.Porta,
        Usuario = c.Usuario,
        Remetente = c.Remetente,
        StartTls = c.StartTls,
        HoraResumo = c.HoraResumo,
        SomenteDiasUteis = c.SomenteDiasUteis,
        LembreteHoras = c.LembreteHoras,
        NotificarSimulacao = c.NotificarSimulacao,
        UrlWeb = c.UrlWeb,
    };

    public ConfiguracaoNotificacao ParaConfiguracao(byte[]? senhaCifrada) => new()
    {
        Ativo = Ativo,
        Servidor = Limpo(Servidor),
        Porta = Porta,
        Usuario = Limpo(Usuario),
        SenhaCifrada = senhaCifrada,
        Remetente = Limpo(Remetente),
        StartTls = StartTls,
        HoraResumo = HoraResumo,
        SomenteDiasUteis = SomenteDiasUteis,
        LembreteHoras = LembreteHoras,
        NotificarSimulacao = NotificarSimulacao,
        UrlWeb = Limpo(UrlWeb),
    };

    private static string? Limpo(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}

public sealed class DestinatarioForm
{
    /// <summary>0 = novo.</summary>
    public long Id { get; set; }

    [Required(ErrorMessage = "Informe o e-mail.")]
    [EmailAddress(ErrorMessage = "E-mail inválido.")]
    [StringLength(200, ErrorMessage = "Use até 200 caracteres.")]
    [Display(Name = "E-mail")]
    public string? Email { get; set; }

    [Display(Name = "Empresa", Description = "Vazio = todas as empresas.")]
    public int? Cdempresa { get; set; }

    [Display(Name = "Recebe falhas")]
    public bool RecebeFalhas { get; set; } = true;

    [Display(Name = "Recebe resumo de pendências")]
    public bool RecebeResumo { get; set; } = true;

    [Display(Name = "Recebe erros da aplicação", Description = "Para o suporte: só os erros técnicos, conforme o envio configurado em Erros da aplicação.")]
    public bool RecebeErros { get; set; }
}
