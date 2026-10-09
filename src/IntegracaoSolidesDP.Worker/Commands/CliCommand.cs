namespace IntegracaoSolidesDP.Worker.Commands;

public enum CliMode
{
    /// <summary>Serviço contínuo (Windows Service / systemd / console).</summary>
    Service,
    RunOnce,
    DryRun,
    Discover,
    CheckConfig,
    Reconcile,
    Version,
}

/// <summary>Comandos do executável. Os demais argumentos seguem para a configuração (ex.: --Sync:DryRun=false).</summary>
/// <param name="Mode">O que o executável faz.</param>
/// <param name="Repair">--repair (só no --reconcile).</param>
/// <param name="HostArgs">Argumentos repassados à configuração.</param>
/// <param name="Empresa">--empresa N: só esta empresa (cada empresa é uma conta do Sólides DP); null = todas as habilitadas.</param>
public sealed record CliCommand(CliMode Mode, bool Repair, string[] HostArgs, int? Empresa = null)
{
    public static CliCommand Parse(string[] args)
    {
        var mode = CliMode.Service;
        var repair = false;
        int? empresa = null;
        var rest = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (TryEmpresa(arg, index + 1 < args.Length ? args[index + 1] : null, out var value, out var consumedNext))
            {
                empresa = value;
                index += consumedNext ? 1 : 0;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--run-once":
                    mode = CliMode.RunOnce;
                    break;
                case "--dry-run":
                    mode = CliMode.DryRun;
                    break;
                case "--discover":
                    mode = CliMode.Discover;
                    break;
                case "--check-config":
                    mode = CliMode.CheckConfig;
                    break;
                case "--reconcile":
                    mode = CliMode.Reconcile;
                    break;
                case "--version":
                    mode = CliMode.Version;
                    break;
                case "--repair":
                    repair = true;
                    break;
                case "--help" or "-h" or "/?":
                    Console.WriteLine(Usage);
                    Environment.Exit(0);
                    break;
                default:
                    rest.Add(arg);
                    break;
            }
        }

        if (mode == CliMode.DryRun)
        {
            // Força o dry-run também na validação (dispensa token e go-live).
            rest.Add("--Sync:DryRun=true");
        }

        return new CliCommand(mode, repair, rest.ToArray(), empresa);
    }

    /// <summary>Aceita "--empresa 15" e "--empresa=15".</summary>
    private static bool TryEmpresa(string arg, string? next, out int? value, out bool consumedNext)
    {
        value = null;
        consumedNext = false;
        string? text;
        if (string.Equals(arg, "--empresa", StringComparison.OrdinalIgnoreCase))
        {
            text = next;
            consumedNext = true;
        }
        else if (arg.StartsWith("--empresa=", StringComparison.OrdinalIgnoreCase))
        {
            text = arg["--empresa=".Length..];
        }
        else
        {
            return false;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"--empresa precisa do código da empresa (ex.: --empresa 15); recebido '{text}'.");
        }

        value = parsed;
        return true;
    }

    public const string Usage = """
        IntegracaoSolidesDP — RHSenso → Sólides DP

        Sem argumentos: roda como serviço, na frequência de Execution:Interval / Execution:TimesOfDay.

          --check-config   valida a configuração, o banco e (se houver token) o acesso ao Sólides DP de cada empresa
          --discover       lista empresas, escalas, regras de ponto e motivos de ajuste da conta do Sólides DP
          --dry-run        uma execução simulada (nada é gravado no Sólides DP) e gera o relatório
          --run-once       uma execução com a configuração atual (o dry-run da configuração decide se é real)
          --reconcile      confere no DP os colaboradores vinculados; com --repair os ausentes voltam a ser procurados pelo CPF
          --version        mostra a versão instalada

          --empresa N      só a empresa N (cada empresa é uma conta do Sólides DP). Obrigatório em --discover e
                           --reconcile quando há mais de uma empresa habilitada.

        Qualquer chave de configuração pode ser passada como --Secao:Chave=valor.
        """;
}
