using IntegracaoSolidesDP.Web.Models;
using IntegracaoSolidesDP.Worker.Management;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Scheduling;

namespace IntegracaoSolidesDP.Tests.Scheduling;

/// <summary>Agenda das execuções pelas regras gerais da Web: cálculo da próxima execução, leitura dos horários e o formulário.</summary>
public sealed class AgendaTests
{
    // 12/10/2026 é uma segunda-feira.
    private static DateTimeOffset Bahia(int d, int h, int mi) => new(2026, 10, d, h, mi, 0, TimeSpan.FromHours(-3));

    private static readonly ExecutionSchedule Arquivo = new(new ExecutionOptions { Interval = TimeSpan.FromMinutes(30) }, TestData.Bahia);

    private static RegimeExecucao Web(AgendaModo modo, int intervalo = 30, string[]? horarios = null, DayOfWeek[]? dias = null)
    {
        var options = new SyncOptions { AgendaModo = modo, AgendaIntervaloMinutos = intervalo };
        if (horarios is not null)
        {
            options.AgendaHorarios = horarios;
        }

        if (dias is not null)
        {
            options.AgendaDiasSemana = dias;
        }

        return RegimeExecucao.DaConfiguracao(options)!;
    }

    [Fact]
    public void Every_monday_at_8()
    {
        var regime = Web(AgendaModo.Horarios, horarios: ["08:00"], dias: [DayOfWeek.Monday]);

        Arquivo.Proxima(regime, Bahia(14, 10, 0)).Should().Be(Bahia(19, 8, 0), "quarta-feira: a próxima é a segunda seguinte");
        Arquivo.Proxima(regime, Bahia(19, 7, 59)).Should().Be(Bahia(19, 8, 0));
        Arquivo.Proxima(regime, Bahia(19, 8, 0)).Should().Be(Bahia(26, 8, 0));
        regime.Descricao.Should().Contain("às 08:00").And.Contain("segunda").And.Contain("Web");
    }

    [Fact]
    public void Every_day_at_10()
    {
        var regime = Web(AgendaModo.Horarios, horarios: ["10:00"]);

        Arquivo.Proxima(regime, Bahia(14, 9, 0)).Should().Be(Bahia(14, 10, 0));
        Arquivo.Proxima(regime, Bahia(14, 10, 0)).Should().Be(Bahia(15, 10, 0));
        regime.Descricao.Should().Contain("todos os dias");
    }

    [Fact]
    public void Every_40_minutes_counts_from_the_end_of_the_last_run_and_never_goes_back_in_time()
    {
        var regime = Web(AgendaModo.Intervalo, intervalo: 40);

        Arquivo.Proxima(regime, Bahia(14, 10, 0)).Should().Be(Bahia(14, 10, 40));
        Arquivo.Proxima(regime, Bahia(14, 10, 5), desde: Bahia(14, 10, 0)).Should().Be(Bahia(14, 10, 40),
            "agenda trocada no meio da espera: conta da última execução");
        Arquivo.Proxima(regime, Bahia(14, 12, 0), desde: Bahia(14, 10, 0)).Should().Be(Bahia(14, 12, 0),
            "se o intervalo novo já passou, executa agora");
        Arquivo.RodarAoIniciar(regime, Bahia(14, 10, 0)).Should().BeTrue();
    }

    [Fact]
    public void Interval_outside_the_marked_days_waits_for_midnight_of_the_next_marked_day()
    {
        var diasUteis = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        var regime = Web(AgendaModo.Intervalo, intervalo: 40, dias: diasUteis);

        Arquivo.Proxima(regime, Bahia(16, 23, 50)).Should().Be(Bahia(19, 0, 0), "sexta 23:50 + 40 min cai no sábado");
        Arquivo.RodarAoIniciar(regime, Bahia(17, 10, 0)).Should().BeFalse("sábado não está marcado");
    }

    [Fact]
    public void Following_the_service_file_or_an_old_version_without_the_agenda_keeps_the_appsettings_schedule()
    {
        RegimeExecucao.DaConfiguracao(new SyncOptions()).Should().BeNull();
        RegimeExecucao.DaConfiguracao(SyncOptionsJson.Deserialize("""{ "DryRun": true }""")).Should().BeNull();
        Arquivo.Regime.Descricao.Should().Contain("a cada 30 min").And.Contain("arquivo do serviço");
    }

    [Theory]
    [InlineData("8", "08:00")]
    [InlineData("8h", "08:00")]
    [InlineData("08:00", "08:00")]
    [InlineData("13h30", "13:30")]
    [InlineData(" 23:59 ", "23:59")]
    public void Times_are_read_in_the_usual_ways(string texto, string esperado)
    {
        HorarioAgenda.TentarLer(texto, out var horario).Should().BeTrue();
        HorarioAgenda.Formatar(horario).Should().Be(esperado);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("8:61")]
    [InlineData("oito")]
    [InlineData("")]
    public void Invalid_times_are_refused(string texto) => HorarioAgenda.TentarLer(texto, out _).Should().BeFalse();

    [Fact]
    public void Form_saves_the_times_normalized_and_the_service_rules_accept_them()
    {
        var form = new ConfiguracaoForm
        {
            AgendaModo = AgendaModo.Horarios,
            AgendaHorarios = "13h30, 8, 08:00",
            AgendaDiasSemana = [DayOfWeek.Monday],
        };
        var erros = new Dictionary<string, string>();

        var options = form.ParaOpcoes(null, erros);

        erros.Should().BeEmpty();
        options.AgendaHorarios.Should().Equal("08:00", "13:30");
        options.AgendaDiasSemana.Should().Equal(DayOfWeek.Monday);
        new SyncOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        ConfiguracaoForm.De(options).AgendaHorarios.Should().Be("08:00, 13:30");
    }

    [Fact]
    public void Form_refuses_a_wrong_time_and_a_schedule_without_days()
    {
        var erros = new Dictionary<string, string>();
        new ConfiguracaoForm { AgendaModo = AgendaModo.Horarios, AgendaHorarios = "08:00, 25:00", AgendaDiasSemana = [DayOfWeek.Monday] }
            .ParaOpcoes(null, erros);
        erros.Should().ContainKey(nameof(ConfiguracaoForm.AgendaHorarios));

        erros.Clear();
        new ConfiguracaoForm { AgendaModo = AgendaModo.Intervalo, AgendaIntervaloMinutos = 40, AgendaDiasSemana = [] }.ParaOpcoes(null, erros);
        erros.Should().ContainKey(nameof(ConfiguracaoForm.AgendaDiasSemana));

        erros.Clear();
        new ConfiguracaoForm { AgendaHorarios = "qualquer coisa", AgendaDiasSemana = [] }.ParaOpcoes(null, erros);
        erros.Should().BeEmpty("seguindo o arquivo do serviço, os campos da agenda não são usados");
    }

    [Fact]
    public void Service_rules_refuse_an_interval_shorter_than_5_minutes()
    {
        var options = new SyncOptions { AgendaModo = AgendaModo.Intervalo, AgendaIntervaloMinutos = 4 };

        new SyncOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }
}
