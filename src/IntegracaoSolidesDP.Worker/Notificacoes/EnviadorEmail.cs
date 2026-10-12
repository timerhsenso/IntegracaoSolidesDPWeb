using System.Net;
using System.Net.Mail;
using System.Text;

namespace IntegracaoSolidesDP.Worker.Notificacoes;

/// <summary>Servidor SMTP já com a senha aberta (só em memória, na hora do envio).</summary>
public sealed record ConfiguracaoSmtp(string Servidor, int Porta, string? Usuario, string? Senha, string Remetente, bool StartTls);

/// <summary>E-mail em HTML para um ou mais destinatários.</summary>
public sealed record MensagemEmail(IReadOnlyList<string> Para, string Assunto, string Html);

public interface IEnviadorEmail
{
    Task EnviarAsync(ConfiguracaoSmtp smtp, MensagemEmail mensagem, CancellationToken ct);
}

/// <summary>
/// Envio pelo SMTP do cliente (System.Net.Mail). STARTTLS na porta 587; SSL direto (porta 465) não é suportado
/// por esta classe — use 587 com STARTTLS, que é o que os provedores oferecem para envio autenticado.
/// </summary>
public sealed class SmtpEnviadorEmail : IEnviadorEmail
{
    private const int TempoLimiteMs = 30_000;

    public async Task EnviarAsync(ConfiguracaoSmtp smtp, MensagemEmail mensagem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(mensagem);
        using var client = new SmtpClient(smtp.Servidor, smtp.Porta)
        {
            EnableSsl = smtp.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = TempoLimiteMs,
        };
        if (!string.IsNullOrWhiteSpace(smtp.Usuario))
        {
            client.Credentials = new NetworkCredential(smtp.Usuario, smtp.Senha);
        }

        using var email = new MailMessage
        {
            From = new MailAddress(smtp.Remetente, "Integração Sólides DP"),
            Subject = mensagem.Assunto,
            SubjectEncoding = Encoding.UTF8,
            Body = mensagem.Html,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true,
        };
        foreach (var para in mensagem.Para)
        {
            email.To.Add(para);
        }

        await client.SendMailAsync(email, ct);
    }
}
