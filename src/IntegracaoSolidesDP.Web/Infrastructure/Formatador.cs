using System.Globalization;
using Microsoft.Extensions.Options;

namespace IntegracaoSolidesDP.Web.Infrastructure;

/// <summary>Datas no fuso configurado (Web:TimeZone) e no formato brasileiro.</summary>
public sealed class Formatador(IOptions<WebOptions> options)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public TimeZoneInfo Fuso { get; } = Obter(options.Value.TimeZone);

    public string Data(DateTimeOffset? valor) =>
        valor is { } v ? TimeZoneInfo.ConvertTime(v, Fuso).ToString("dd/MM/yyyy HH:mm:ss", PtBr) : "—";

    public string Dia(DateOnly dia) => dia.ToString("ddd dd/MM", PtBr);

    public static string Duracao(TimeSpan? duracao) => duracao switch
    {
        null => "—",
        { TotalHours: >= 1 } d => $"{(int)d.TotalHours}h{d.Minutes:00}min",
        { TotalMinutes: >= 1 } d => $"{(int)d.TotalMinutes}min{d.Seconds:00}s",
        { } d => $"{d.TotalSeconds:0.#}s",
    };

    /// <summary>Percentual para CSS (sempre com ponto decimal, independente da cultura do servidor).</summary>
    public static string Percentual(int parte, int total) =>
        (total <= 0 ? 0 : 100.0 * parte / total).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public DateOnly Hoje(TimeProvider clock) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Fuso).DateTime);

    public DateOnly DiaLocal(DateTimeOffset valor) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(valor, Fuso).DateTime);

    private static TimeZoneInfo Obter(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
