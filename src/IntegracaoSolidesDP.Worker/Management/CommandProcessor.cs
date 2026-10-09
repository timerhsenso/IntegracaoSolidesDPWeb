using System.Globalization;
using System.Text.Json;
using IntegracaoSolidesDP.Worker.Commands;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Pipeline;
using IntegracaoSolidesDP.Worker.State;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Worker.Management;

/// <summary>
/// Atende a fila solidesdp.comando (pedidos feitos na Web). Só o serviço fala com a API do
/// Sólides DP: a Web grava o pedido e lê o resultado. Um comando por vez, em ordem de chegada.
/// </summary>
public sealed class CommandProcessor(
    IServiceScopeFactory scopes,
    IStateStore state,
    IManagementStore store,
    IOptions<SyncOptions> syncOptions,
    ILogger<CommandProcessor> logger)
{
    private string InstanceName => syncOptions.Value.InstanceName;

    /// <summary>Na partida do serviço: cria as tabelas e encerra comandos que ficaram "executando".</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await state.EnsureSchemaAsync(ct);
        await store.EnsureSchemaAsync(ct);
        var interrupted = await store.FailInterruptedCommandsAsync(InstanceName, ct);
        if (interrupted > 0)
        {
            logger.LogWarning("{Count} comando(s) interrompido(s) por uma parada do serviço foram marcados como falhou", interrupted);
        }
    }

    /// <summary>Executa todos os comandos pendentes da instância. Devolve quantos foram atendidos.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        var processed = 0;
        while (!ct.IsCancellationRequested && await store.ClaimNextCommandAsync(InstanceName, ct) is { } command)
        {
            processed++;
            logger.LogInformation("Comando {Id} {Type} pedido por {User} (empresa {Empresa})",
                command.Id, command.Type, command.RequestedBy, command.Cdempresa?.ToString(CultureInfo.InvariantCulture) ?? "todas");
            var (status, runId, result) = await ExecuteAsync(command, ct);
            // Mesmo com o serviço parando, o desfecho do comando fica registrado.
            await store.CompleteCommandAsync(command.Id, status, runId, result, CancellationToken.None);
            logger.LogInformation("Comando {Id} {Type}: {Status}", command.Id, command.Type, status);
        }

        return processed;
    }

    private async Task<(string Status, Guid? RunId, string? Result)> ExecuteAsync(ClaimedCommand command, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;

            switch (command.Type)
            {
                case CommandTypes.Run or CommandTypes.DryRun:
                {
                    var summary = await services.GetRequiredService<SyncPipeline>().RunAsync(
                        new RunRequest("web", command.Type == CommandTypes.DryRun ? true : null, command.RequestedBy, command.Cdempresa), ct);
                    var ok = summary.Status is RunStatuses.Completed or RunStatuses.CompletedWithErrors;
                    return (ok ? CommandStatuses.Done : CommandStatuses.Failed,
                        summary.RunId == Guid.Empty ? (Guid?)null : summary.RunId,
                        Describe(summary));
                }

                case CommandTypes.CheckConfig or CommandTypes.Discover or CommandTypes.Reconcile:
                {
                    await using var output = new StringWriter(CultureInfo.InvariantCulture);
                    var operations = ActivatorUtilities.CreateInstance<OperatorCommands>(services, (TextWriter)output);
                    var exitCode = command.Type switch
                    {
                        CommandTypes.CheckConfig => await operations.CheckConfigAsync(ct),
                        CommandTypes.Discover => await operations.DiscoverAsync(command.Cdempresa, ct),
                        // Pela Web só a conferência; o --repair continua sendo decisão de quem opera o servidor.
                        _ => await operations.ReconcileAsync(command.Cdempresa, repair: false, ct),
                    };
                    return (exitCode == 0 ? CommandStatuses.Done : CommandStatuses.Failed, null, output.ToString());
                }

                default:
                    return (CommandStatuses.Failed, null, $"Tipo de comando desconhecido: {command.Type}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (CommandStatuses.Failed, null, "interrompido: o serviço foi parado durante a execução");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Comando {Id} {Type} falhou", command.Id, command.Type);
            return (CommandStatuses.Failed, null, ex.Message);
        }
    }

    private static string Describe(RunSummary summary)
    {
        if (summary.Empresas.Count == 0)
        {
            return DescribeOne(summary);
        }

        return string.Join(Environment.NewLine, summary.Empresas.Select(p =>
            $"empresa {p.Cdempresa?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {DescribeOne(p)}"));
    }

    private static string DescribeOne(RunSummary summary)
    {
        var counts = JsonSerializer.Serialize(summary.Counts);
        return summary.Error is null ? $"{summary.Status} {counts}" : $"{summary.Status}: {summary.Error} {counts}";
    }
}
