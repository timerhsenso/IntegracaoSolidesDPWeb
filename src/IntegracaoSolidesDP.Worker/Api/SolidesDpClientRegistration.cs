using System.Net;
using System.Net.Http.Headers;
using IntegracaoSolidesDP.Worker.Options;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace IntegracaoSolidesDP.Worker.Api;

public static class SolidesDpClientRegistration
{
    /// <summary>Registra o cliente do Sólides DP. <paramref name="configureRetry"/> é um gancho para testes (ex.: zerar o atraso entre tentativas).</summary>
    public static IServiceCollection AddSolidesDpClient(
        this IServiceCollection services,
        Action<HttpStandardResilienceOptions>? configureRetry = null)
    {
        services.AddSingleton(sp => new SolidesDpClientSettings(sp.GetRequiredService<IOptions<SolidesDpOptions>>().Value.SkipUnifiedSync));
        services.AddSingleton<ISolidesDpClient, SolidesDpClient>();

        var retry = services.AddHttpClient(SolidesDpClient.RetryClientName, ConfigureClient);
        retry.AddStandardResilienceHandler();
        services.AddOptions<HttpStandardResilienceOptions>(retry.Name)
            .Configure<IOptions<SolidesDpOptions>>((resilience, options) =>
            {
                var attempt = TimeSpan.FromSeconds(Math.Max(options.Value.TimeoutSeconds, 1));
                resilience.AttemptTimeout.Timeout = attempt;
                resilience.TotalRequestTimeout.Timeout = attempt * 4;
                resilience.CircuitBreaker.SamplingDuration = attempt * 2;
                resilience.Retry.MaxRetryAttempts = 3;
                resilience.Retry.BackoffType = DelayBackoffType.Exponential;
                resilience.Retry.UseJitter = true;
                resilience.Retry.ShouldRetryAfterHeader = true;
                // Transientes + 429. Um 4xx de validação não é retentado: a API recusaria de novo.
                resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
                    HttpClientResiliencePredicates.IsTransient(args.Outcome)
                    || args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests);
                configureRetry?.Invoke(resilience);
            });

        // Sem retry: só timeout. Usado onde reenviar poderia duplicar o registro.
        services.AddHttpClient(SolidesDpClient.NoRetryClientName, ConfigureClient)
            .AddResilienceHandler("timeout-only", (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<SolidesDpOptions>>().Value;
                pipeline.AddTimeout(TimeSpan.FromSeconds(Math.Max(options.TimeoutSeconds, 1)));
            });

        return services;
    }

    private static void ConfigureClient(IServiceProvider services, HttpClient client)
    {
        var options = services.GetRequiredService<IOptions<SolidesDpOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        // O timeout é do pipeline de resiliência; o do HttpClient atrapalharia os retries.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            // Documentação do DP: "Authorization: Basic seu_token" (o token já vem pronto, sem codificar).
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", options.Token);
        }
    }
}
