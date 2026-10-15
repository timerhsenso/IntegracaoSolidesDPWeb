namespace IntegracaoSolidesDP.Worker.Notificacoes;

/// <summary>
/// Configuração dos avisos por e-mail (solidesdp.notificacao_config, uma linha só). Editada na Web (tela Notificações);
/// o serviço lê a cada execução. A senha do SMTP fica cifrada com a mesma chave dos tokens.
/// </summary>
public sealed record ConfiguracaoNotificacao
{
    /// <summary>Liga os avisos. Desligado, nada é enviado (o teste pela Web funciona mesmo assim).</summary>
    public bool Ativo { get; init; }

    public string? Servidor { get; init; }

    public int Porta { get; init; } = 587;

    public string? Usuario { get; init; }

    /// <summary>Senha do SMTP cifrada (ITokenProtector). Null = sem senha.</summary>
    public byte[]? SenhaCifrada { get; init; }

    /// <summary>Endereço que aparece como remetente (o servidor pode exigir que seja o do usuário).</summary>
    public string? Remetente { get; init; }

    /// <summary>STARTTLS (porta 587). Desligado = conexão sem criptografia.</summary>
    public bool StartTls { get; init; } = true;

    /// <summary>Hora local (Execution:TimeZone) do resumo diário das pendências.</summary>
    public TimeSpan HoraResumo { get; init; } = new(8, 0, 0);

    /// <summary>Resumo só de segunda a sexta.</summary>
    public bool SomenteDiasUteis { get; init; } = true;

    /// <summary>Com a falha continuando, um lembrete a cada tantas horas.</summary>
    public int LembreteHoras { get; init; } = 24;

    /// <summary>Simulações também avisam (desligado = só execuções reais).</summary>
    public bool NotificarSimulacao { get; init; }

    /// <summary>Endereço da Web (ex.: https://srvhomologacao:8443), para os links dos e-mails. Vazio = sem link.</summary>
    public string? UrlWeb { get; init; }

    /// <summary>Quando o último resumo diário foi processado (enviado ou sem novidade).</summary>
    public DateTimeOffset? UltimoResumo { get; init; }

    public string? AtualizadoPor { get; init; }

    public DateTimeOffset? AtualizadoEm { get; init; }

    /// <summary>Tem o mínimo para enviar: servidor, porta e remetente.</summary>
    public bool Completa => !string.IsNullOrWhiteSpace(Servidor) && Porta > 0 && !string.IsNullOrWhiteSpace(Remetente);
}

/// <summary>Quem recebe os avisos (solidesdp.notificacao_destinatario). Empresa nula = todas as empresas.</summary>
public sealed record DestinatarioNotificacao
{
    public long Id { get; init; }

    public string Email { get; init; } = string.Empty;

    public int? Cdempresa { get; init; }

    /// <summary>Recebe na hora as falhas (e o "voltou ao normal").</summary>
    public bool RecebeFalhas { get; init; }

    /// <summary>Recebe o resumo diário das pendências.</summary>
    public bool RecebeResumo { get; init; }

    /// <summary>Recebe os erros da aplicação (ex.: o suporte). Erro vale para todas as empresas.</summary>
    public bool RecebeErros { get; init; }

    public bool Ativo { get; init; } = true;

    /// <summary>Vale para esta empresa (nula = todas).</summary>
    public bool Atende(int cdempresa) => Cdempresa is null || Cdempresa == cdempresa;
}

/// <summary>Situação de cada empresa para os avisos de falha (solidesdp.notificacao_estado): avisa só na mudança.</summary>
public sealed record EstadoNotificacao
{
    public int Cdempresa { get; init; }

    public bool EmFalha { get; init; }

    public DateTimeOffset? FalhaDesde { get; init; }

    public DateTimeOffset? UltimoAviso { get; init; }
}

/// <summary>Pendência vista na última execução de uma empresa (item bloqueado ou ignorado por dado do RHSenso).</summary>
/// <param name="Chave">"entidade|registro|código": a mesma pendência tem sempre a mesma chave.</param>
/// <param name="Rotulo">Registro (ex.: "15-00901288").</param>
/// <param name="Mensagem">Mensagem do item.</param>
public sealed record PendenciaAtual(string Chave, string Rotulo, string Mensagem);

/// <summary>Pendência ainda aberta (solidesdp.notificacao_pendencia), para o resumo diário.</summary>
public sealed record PendenciaAberta
{
    public int Cdempresa { get; init; }

    public string Chave { get; init; } = string.Empty;

    public string Rotulo { get; init; } = string.Empty;

    public string Mensagem { get; init; } = string.Empty;

    /// <summary>Quando apareceu (ou reapareceu depois de resolvida): define o que é "novo" no resumo.</summary>
    public DateTimeOffset PrimeiraVez { get; init; }
}

/// <summary>Um e-mail enviado ou tentado (solidesdp.notificacao_envio).</summary>
public sealed record EnvioNotificacao
{
    public long Id { get; init; }

    public DateTimeOffset EnviadoEm { get; init; }

    /// <summary>Ver <see cref="TiposNotificacao"/>.</summary>
    public string Tipo { get; init; } = string.Empty;

    public int? Cdempresa { get; init; }

    /// <summary>Destinatários separados por vírgula.</summary>
    public string Para { get; init; } = string.Empty;

    public string Assunto { get; init; } = string.Empty;

    public bool Ok { get; init; }

    public string? Erro { get; init; }
}

/// <summary>Valores de solidesdp.notificacao_envio.tipo.</summary>
public static class TiposNotificacao
{
    public const string Falha = "falha";
    public const string Lembrete = "lembrete";
    public const string Normalizou = "normalizou";
    public const string Resumo = "resumo";
    public const string Teste = "teste";

    /// <summary>Conferência semanal DP x RHSenso com divergências.</summary>
    public const string Conferencia = "conferencia";

    /// <summary>Primeiro aviso de um erro da aplicação (envio automático).</summary>
    public const string Erro = "erro";

    /// <summary>Resumo dos erros que continuam acontecendo.</summary>
    public const string ErroResumo = "erro_resumo";

    /// <summary>Erro enviado pelo botão "Enviar ao suporte".</summary>
    public const string ErroManual = "erro_manual";

    /// <summary>Aviso de que o limite diário de e-mails de erro foi atingido.</summary>
    public const string ErroLimite = "erro_limite";

    /// <summary>Os e-mails de erro que contam no limite diário.</summary>
    public static readonly IReadOnlyList<string> ContamNoLimite = [Erro, ErroResumo, ErroManual];
}
