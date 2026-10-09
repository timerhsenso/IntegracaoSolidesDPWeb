namespace IntegracaoSolidesDP.Worker.Mapping;

/// <summary>
/// Regra do "Código Externo" (externalId) do colaborador no Sólides DP. O cliente usa esse campo em outro
/// sistema, então a integração só o preenche ou corrige quando ele claramente é (ou deveria ser) a matrícula:
/// <list type="bullet">
/// <item>vazio, nulo ou só zeros → a matrícula;</item>
/// <item>igual à matrícula → nada muda;</item>
/// <item>8 dígitos, mas diferente da matrícula (ex.: "00000024" de outra matrícula) → corrige para a matrícula;</item>
/// <item>qualquer outra coisa → não mexe (é o código de outro sistema).</item>
/// </list>
/// O campo Matrícula do Sólides DP é sempre a matrícula do RHSenso.
/// </summary>
public static class CodigoExterno
{
    public const int DigitosMatricula = 8;

    /// <summary>O valor a enviar no externalId, dado o que está hoje no Sólides DP.</summary>
    public static string ParaEnviar(string? atual, string matricula)
    {
        var correta = matricula.Trim();
        var valor = atual?.Trim();

        if (string.IsNullOrEmpty(valor) || valor.All(c => c == '0'))
        {
            return correta;
        }

        if (string.Equals(valor, correta, StringComparison.Ordinal))
        {
            return correta;
        }

        if (valor.Length == DigitosMatricula && valor.All(char.IsAsciiDigit))
        {
            return correta;
        }

        return valor;
    }

    /// <summary>Descrição da mudança para o relatório; null quando o Código Externo fica como está.</summary>
    public static string? DescreverMudanca(string? atual, string enviar)
    {
        var valor = atual?.Trim();
        if (string.Equals(valor, enviar, StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrEmpty(valor)
            ? $"Código Externo vazio → {enviar}"
            : $"Código Externo {valor} → {enviar}";
    }
}
