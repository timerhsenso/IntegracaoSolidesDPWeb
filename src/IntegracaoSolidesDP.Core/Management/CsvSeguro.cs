namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Proteção dos CSV que o RH abre no Excel contra injeção de fórmula: texto vindo de fora (nome no Sólides DP, mensagem
/// da API) que começa com = + - @ TAB ou CR ganha um apóstrofo na frente e é mostrado como texto, sem ser executado.
/// </summary>
public static class CsvSeguro
{
    public static string Neutralizar(string valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        if (valor.Length == 0)
        {
            return valor;
        }

        var perigoso = valor[0] switch
        {
            '=' or '+' or '@' or '\t' or '\r' => true,
            // "-" sozinho e números negativos são inofensivos; "-algo" pode ser fórmula (ex.: -2+3, -cmd|...).
            '-' => valor.Length > 1 && !char.IsAsciiDigit(valor[1]) && valor[1] != ' ',
            _ => false,
        };
        return perigoso ? "'" + valor : valor;
    }
}
