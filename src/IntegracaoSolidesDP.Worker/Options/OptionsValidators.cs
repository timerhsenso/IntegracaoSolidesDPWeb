using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Options;

internal sealed class ExecutionOptionsValidator : IValidateOptions<ExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, ExecutionOptions options)
    {
        var errors = new List<string>();
        var hasInterval = options.Interval is not null;
        var hasTimes = options.TimesOfDay is { Count: > 0 };

        if (hasInterval == hasTimes)
        {
            errors.Add("Informe exatamente um entre Execution:Interval e Execution:TimesOfDay.");
        }

        if (options.Interval is { } interval && interval < TimeSpan.FromMinutes(1))
        {
            errors.Add("Execution:Interval deve ser de pelo menos 1 minuto.");
        }

        if (options.TimesOfDay is { } times && times.Any(t => t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)))
        {
            errors.Add("Execution:TimesOfDay deve conter horários entre 00:00 e 23:59.");
        }

        if (!TryFindTimeZone(options.TimeZone, out _))
        {
            errors.Add($"Execution:TimeZone '{options.TimeZone}' não é um fuso conhecido.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool TryFindTimeZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

internal sealed class SolidesDpOptionsValidator(IHostEnvironment environment, IOptions<SyncOptions> sync)
    : IValidateOptions<SolidesDpOptions>
{
    public ValidateOptionsResult Validate(string? name, SolidesDpOptions options)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"SolidesDP:BaseUrl '{options.BaseUrl}' deve ser uma URL http(s) absoluta.");
        }
        else if (SolidesDpOptions.IsProductionHost(options.BaseUrl)
                 && !environment.IsProduction()
                 && !options.AllowProductionApiOutsideProduction)
        {
            // O Sólides DP não tem homologação: fora de Production, a API real só com opt-in explícito.
            errors.Add(
                $"SolidesDP:BaseUrl aponta para a API real ({uri.Host}) no ambiente '{environment.EnvironmentName}'. " +
                "O Sólides DP não tem homologação; use o fake (http://localhost:5080) ou defina " +
                "SolidesDP:AllowProductionApiOutsideProduction=true para uma execução supervisionada.");
        }

        if (options.TimeoutSeconds is < 1 or > 300)
        {
            errors.Add("SolidesDP:TimeoutSeconds deve estar entre 1 e 300.");
        }

        if (!sync.Value.DryRun && string.IsNullOrWhiteSpace(options.Token))
        {
            errors.Add("SolidesDP:Token é obrigatório fora do dry-run.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class ManagementOptionsValidator : IValidateOptions<ManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, ManagementOptions options) =>
        options.IntervaloComandos < TimeSpan.FromSeconds(1) || options.IntervaloComandos > TimeSpan.FromMinutes(10)
            ? ValidateOptionsResult.Fail("Gestao:IntervaloComandos deve estar entre 00:00:01 e 00:10:00.")
            : ValidateOptionsResult.Success;
}
