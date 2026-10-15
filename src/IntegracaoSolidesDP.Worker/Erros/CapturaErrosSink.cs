using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace IntegracaoSolidesDP.Worker.Erros;

/// <summary>
/// Sink do Serilog do serviço: todo evento de nível Error ou acima vai para a <see cref="FilaErros"/> (gravada em
/// solidesdp.erro_aplicacao pelo <see cref="GravadorErros"/>). Nunca lança nem espera o banco.
/// </summary>
public sealed class CapturaErrosSink(FilaErros fila) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        var contexto = Texto(logEvent, "SourceContext");
        if (ErroDeLog.Ignorar(contexto))
        {
            return;
        }

        try
        {
            fila.Enfileirar(ErroDeLog.Criar(
                OrigensErro.Servico, contexto, logEvent.MessageTemplate.Text, logEvent.RenderMessage(CultureInfo.InvariantCulture),
                logEvent.Exception, Empresa(logEvent), logEvent.Timestamp));
        }
        catch (Exception)
        {
            // Sem registro: o evento continua no arquivo de log.
        }
    }

    internal static string? Texto(LogEvent logEvent, string nome) =>
        logEvent.Properties.TryGetValue(nome, out var valor) && valor is ScalarValue { Value: string texto } ? texto : null;

    internal static int? Empresa(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("Empresa", out var valor) || valor is not ScalarValue scalar)
        {
            return null;
        }

        return scalar.Value switch
        {
            int numero => numero,
            short numero => numero,
            long numero when numero is >= int.MinValue and <= int.MaxValue => (int)numero,
            string texto when int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero) => numero,
            _ => (int?)null,
        };
    }
}
