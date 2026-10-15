using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// Limpeza do texto de um erro antes de gravar e de enviar ao suporte (que é de fora do cliente): CPF mascarado e
/// token, senha e "Authorization" apagados. E a assinatura que agrupa as ocorrências do mesmo erro.
/// </summary>
public static partial class ErroTexto
{
    /// <summary>Tira dados pessoais e segredos e corta no tamanho máximo.</summary>
    public static string? Limpar(string? texto, int tamanhoMaximo)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return texto;
        }

        var limpo = CpfRegex().Replace(texto, "$1.***.***-$4");
        limpo = AutorizacaoRegex().Replace(limpo, "$1 ***");
        limpo = SenhaRegex().Replace(limpo, "$1=***");
        limpo = TokenRegex().Replace(limpo, "$1***");
        return limpo.Length <= tamanhoMaximo ? limpo : limpo[..tamanhoMaximo];
    }

    /// <summary>
    /// Assinatura do erro: origem, fonte, tipo da exceção e a mensagem sem números nem ids (o mesmo erro em execuções
    /// diferentes cai na mesma linha). SHA-256 em hexadecimal.
    /// </summary>
    public static string Assinatura(string origem, string? fonte, string? tipo, string mensagem)
    {
        var normalizada = NumeroRegex().Replace(GuidRegex().Replace(mensagem, "#"), "#");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', origem, fonte, tipo, normalizada)));
        return Convert.ToHexString(bytes);
    }

    [GeneratedRegex(@"(?<!\d)(\d{3})\.?(\d{3})\.?(\d{3})-?(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CpfRegex();

    [GeneratedRegex(@"\b(Basic|Bearer)\s+[A-Za-z0-9+/=._\-]{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AutorizacaoRegex();

    [GeneratedRegex(@"\b(Password|Pwd|Senha)\s*=\s*[^;""'\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SenhaRegex();

    [GeneratedRegex(@"(""?token""?\s*[:=]\s*""?)[^""&;\s]{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumeroRegex();
}
