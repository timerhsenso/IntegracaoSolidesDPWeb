using IntegracaoSolidesDP.Worker.Mapping;
using IntegracaoSolidesDP.Worker.Options;
using IntegracaoSolidesDP.Worker.Source;

namespace IntegracaoSolidesDP.Tests.Mapping;

public sealed class VacationMapperTests
{
    private static VacationRow Row(int situacao = 6, int dias = 30) => new()
    {
        Id = Guid.Parse("8440408b-44f5-ef11-b844-ef1ef4af3d33"),
        Nomatric = "00901482",
        Cdempresa = 14,
        Cdfilial = 1,
        Inicio = new DateTime(2026, 6, 1),
        Fim = new DateTime(2026, 6, 30),
        Dias = dias,
        Situacao = situacao,
    };

    [Fact]
    public void Default_end_date_is_midnight_of_the_next_day_like_the_supplier_docs_example()
    {
        var mapping = new VacationMapper(TestData.Dates, new SyncOptions()).Map(Row());

        // Exemplo da documentação: 30 dias de junho/2019 → startDate 1559358000000, endDate 1561950000000 (01/07 00:00 BRT).
        mapping.StartDate.Should().Be(new DateTimeOffset(2026, 6, 1, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        mapping.EndDate.Should().Be(new DateTimeOffset(2026, 7, 1, 3, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Docs_example_dates_match_the_default_rule()
    {
        var row = Row() with { Inicio = new DateTime(2019, 6, 1), Fim = new DateTime(2019, 6, 30) };

        var mapping = new VacationMapper(new EpochDates(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")), new SyncOptions()).Map(row);

        mapping.StartDate.Should().Be(1559358000000);
        mapping.EndDate.Should().Be(1561950000000);
    }

    [Theory]
    [InlineData(FeriasEndDateMode.InicioDoUltimoDia, 2026, 6, 30, 3, 0, 0, 0)]
    [InlineData(FeriasEndDateMode.FimDoUltimoDia, 2026, 7, 1, 2, 59, 59, 999)]
    public void End_date_mode_is_configurable(FeriasEndDateMode mode, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        var mapping = new VacationMapper(TestData.Dates, new SyncOptions { FeriasEndDateMode = mode }).Map(Row());

        mapping.EndDate.Should().Be(new DateTimeOffset(y, mo, d, h, mi, s, ms, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(2, VacationDecision.Send, "APROVADO")]
    [InlineData(3, VacationDecision.Send, "APROVADO")]
    [InlineData(4, VacationDecision.Send, "APROVADO")]
    [InlineData(6, VacationDecision.Send, "APROVADO")]
    [InlineData(1, VacationDecision.Skip, null)]
    [InlineData(7, VacationDecision.Cancel, null)]
    [InlineData(9, VacationDecision.Skip, null)]
    public void Status_follows_rhsenso_table_42(int flconfirm, VacationDecision decision, string? status)
    {
        var mapping = new VacationMapper(TestData.Dates, new SyncOptions()).Map(Row(flconfirm));

        mapping.Decision.Should().Be(decision);
        mapping.Status.Should().Be(status);
    }

    [Fact]
    public void Scheduled_vacations_can_be_sent_as_pending()
    {
        var mapping = new VacationMapper(TestData.Dates, new SyncOptions { FeriasEnviarProgramadas = true }).Map(Row(1));

        mapping.Decision.Should().Be(VacationDecision.Send);
        mapping.Status.Should().Be("PENDENTE");
    }

    [Fact]
    public void Day_count_mismatch_warns_but_still_sends_the_enjoyed_period()
    {
        var mapping = new VacationMapper(TestData.Dates, new SyncOptions()).Map(Row(dias: 20) with { Abono = 10 });

        mapping.Decision.Should().Be(VacationDecision.Send);
        mapping.Warnings.Should().ContainSingle().Which.Should().Be("days_mismatch:periodo=30,qtdiasfe=20,abono=10");
    }

    [Fact]
    public void Marker_identifies_the_rhsenso_row()
    {
        VacationMapper.Marker(Row().Id).Should().Be("RHSenso:8440408b-44f5-ef11-b844-ef1ef4af3d33");
    }
}
