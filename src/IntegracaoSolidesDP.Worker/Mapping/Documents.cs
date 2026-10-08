namespace IntegracaoSolidesDP.Worker.Mapping;

/// <summary>
/// Validação de CPF e PIS. No func1 alguns CPFs perderam os zeros à esquerda (ex.: 9 dígitos):
/// completa com zeros e só aceita se o dígito verificador fechar.
/// </summary>
public static class Documents
{
    public static (string? Value, string? Warning) NormalizeCpf(string? raw)
    {
        var digits = EmployeeMapper.Digits(raw);
        if (digits is null)
        {
            return (null, "missing_cpf");
        }

        // func1.nocpf é varchar(11): CPF gravado com máscara ("###.###.###-##") perde os dígitos verificadores.
        if (digits.Length < 11 && raw!.IndexOfAny(['.', '-']) >= 0)
        {
            return (null, "cpf_truncado_no_rhsenso: corrigir func1.nocpf (gravado com máscara, faltam dígitos)");
        }

        if (digits.Length is < 9 or > 11)
        {
            return (null, $"invalid_cpf_length:{digits.Length}");
        }

        var padded = digits.PadLeft(11, '0');
        if (!IsValidCpf(padded))
        {
            return (null, "invalid_cpf_check_digit");
        }

        return (padded, padded.Length != digits.Length ? "cpf_zero_padded" : null);
    }

    public static (string? Value, string? Warning) NormalizePis(string? raw)
    {
        var digits = EmployeeMapper.Digits(raw);
        if (digits is null)
        {
            return (null, null);
        }

        if (digits.Length is < 9 or > 11)
        {
            return (null, $"invalid_pis_length:{digits.Length}");
        }

        var padded = digits.PadLeft(11, '0');
        return IsValidPis(padded) ? (padded, null) : (null, "invalid_pis_check_digit");
    }

    public static bool IsValidCpf(string cpf)
    {
        if (cpf.Length != 11 || !cpf.All(char.IsAsciiDigit) || cpf.Distinct().Count() == 1)
        {
            return false;
        }

        var d = cpf.Select(c => c - '0').ToArray();
        var first = CheckDigit(d, 9, 10);
        var second = CheckDigit(d, 10, 11);
        return d[9] == first && d[10] == second;

        static int CheckDigit(int[] digits, int count, int startWeight)
        {
            var sum = 0;
            for (var i = 0; i < count; i++)
            {
                sum += digits[i] * (startWeight - i);
            }

            var rest = sum % 11;
            return rest < 2 ? 0 : 11 - rest;
        }
    }

    public static bool IsValidPis(string pis)
    {
        if (pis.Length != 11 || !pis.All(char.IsAsciiDigit) || pis.Distinct().Count() == 1)
        {
            return false;
        }

        int[] weights = [3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += (pis[i] - '0') * weights[i];
        }

        var rest = 11 - (sum % 11);
        var digit = rest is 10 or 11 ? 0 : rest;
        return pis[10] - '0' == digit;
    }
}
