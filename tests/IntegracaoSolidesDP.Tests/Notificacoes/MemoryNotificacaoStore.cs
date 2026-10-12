using IntegracaoSolidesDP.Worker.Notificacoes;

namespace IntegracaoSolidesDP.Tests.Notificacoes;

/// <summary>Mesmas regras do SqlNotificacaoStore, em memória (o SQL tem testes próprios).</summary>
internal sealed class MemoryNotificacaoStore : INotificacaoStore
{
    private readonly Dictionary<(int, string), (PendenciaAberta Pendencia, DateTimeOffset UltimaVez, DateTimeOffset? Resolvida)> _pendencias = [];
    private readonly Dictionary<int, EstadoNotificacao> _estados = [];
    private long _proximoId = 1;

    public ConfiguracaoNotificacao? Configuracao { get; set; }

    public List<DestinatarioNotificacao> Destinatarios { get; } = [];

    public List<EnvioNotificacao> Envios { get; } = [];

    public Task<ConfiguracaoNotificacao?> ConfiguracaoAsync(CancellationToken ct) => Task.FromResult(Configuracao);

    public Task SalvarConfiguracaoAsync(ConfiguracaoNotificacao configuracao, bool trocarSenha, string usuario, CancellationToken ct)
    {
        Configuracao = configuracao with
        {
            SenhaCifrada = trocarSenha ? configuracao.SenhaCifrada : Configuracao?.SenhaCifrada,
            UltimoResumo = Configuracao?.UltimoResumo,
            AtualizadoPor = usuario,
        };
        return Task.CompletedTask;
    }

    public Task MarcarResumoAsync(DateTimeOffset quando, CancellationToken ct)
    {
        Configuracao = Configuracao! with { UltimoResumo = quando };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DestinatarioNotificacao>> DestinatariosAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DestinatarioNotificacao>>(Destinatarios.ToList());

    public Task<long> SalvarDestinatarioAsync(DestinatarioNotificacao destinatario, string usuario, CancellationToken ct)
    {
        if (destinatario.Id > 0)
        {
            Destinatarios[Destinatarios.FindIndex(d => d.Id == destinatario.Id)] = destinatario;
            return Task.FromResult(destinatario.Id);
        }

        var novo = destinatario with { Id = _proximoId++ };
        Destinatarios.Add(novo);
        return Task.FromResult(novo.Id);
    }

    public Task<EstadoNotificacao?> EstadoAsync(int cdempresa, CancellationToken ct) =>
        Task.FromResult(_estados.GetValueOrDefault(cdempresa));

    public Task SalvarEstadoAsync(EstadoNotificacao estado, CancellationToken ct)
    {
        _estados[estado.Cdempresa] = estado;
        return Task.CompletedTask;
    }

    public Task AtualizarPendenciasAsync(int cdempresa, IReadOnlyList<PendenciaAtual> atuais, DateTimeOffset agora, CancellationToken ct)
    {
        foreach (var atual in atuais)
        {
            var chave = (cdempresa, atual.Chave);
            var primeira = _pendencias.TryGetValue(chave, out var existente) && existente.Resolvida is null
                ? existente.Pendencia.PrimeiraVez
                : agora;
            _pendencias[chave] = (new PendenciaAberta
            {
                Cdempresa = cdempresa,
                Chave = atual.Chave,
                Rotulo = atual.Rotulo,
                Mensagem = atual.Mensagem,
                PrimeiraVez = primeira,
            }, agora, null);
        }

        foreach (var (chave, valor) in _pendencias.Where(p => p.Key.Item1 == cdempresa && p.Value.Resolvida is null && p.Value.UltimaVez < agora).ToList())
        {
            _pendencias[chave] = valor with { Resolvida = agora };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendenciaAberta>> PendenciasAbertasAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PendenciaAberta>>(_pendencias.Values.Where(p => p.Resolvida is null).Select(p => p.Pendencia).ToList());

    public Task RegistrarEnvioAsync(EnvioNotificacao envio, CancellationToken ct)
    {
        Envios.Add(envio with { Id = Envios.Count + 1 });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EnvioNotificacao>> EnviosRecentesAsync(int quantidade, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EnvioNotificacao>>(Envios.AsEnumerable().Reverse().Take(quantidade).ToList());
}

/// <summary>Guarda os e-mails em vez de enviar. <see cref="Falhar"/> simula o servidor recusando.</summary>
internal sealed class FakeEnviadorEmail : IEnviadorEmail
{
    public List<(ConfiguracaoSmtp Smtp, MensagemEmail Mensagem)> Enviados { get; } = [];

    public string? Falhar { get; set; }

    public Task EnviarAsync(ConfiguracaoSmtp smtp, MensagemEmail mensagem, CancellationToken ct)
    {
        if (Falhar is { } motivo)
        {
            throw new InvalidOperationException(motivo);
        }

        Enviados.Add((smtp, mensagem));
        return Task.CompletedTask;
    }
}
