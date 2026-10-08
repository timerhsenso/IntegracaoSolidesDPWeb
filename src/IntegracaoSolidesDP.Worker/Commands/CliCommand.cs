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
public sealed record CliCommand(CliMode Mode, bool Repair, string[] HostArgs)
{
    public static CliCommand Parse(string[] args)
    {
        var mode = CliMode.Service;
        var repair = false;
        var rest = new List<string>();

        foreach (var arg in args)
        {
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

        return new CliCommand(mode, repair, rest.ToArray());
    }

    public const string Usage = """
        IntegracaoSolidesDP — RHSenso → Sólides DP

        Sem argumentos: roda como serviço, na frequência de Execution:Interval / Execution:TimesOfDay.

          --check-config   valida a configuração, o banco e (se houver token) o acesso ao Sólides DP
          --discover       lista empresas, escalas, regras de ponto e motivos de ajuste do Sólides DP
          --dry-run        uma execução simulada (sem chamar a API) e gera o relatório
          --run-once       uma execução com a configuração atual (Sync:DryRun decide se é real)
          --reconcile      confere no DP os colaboradores já enviados; com --repair força o reenvio dos ausentes
          --version        mostra a versão instalada

        Qualquer chave de configuração pode ser passada como --Secao:Chave=valor.
        """;
}
