using MarqSpec.TradingCopilot.Api.Flatten;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Flatten;

/// <summary>
/// The auto-flatten schedule source (gh#185, R-13). The safety-relevant behaviour: auto-flatten is <b>on by
/// default, per market</b>, and disabling a market is only ever an <b>explicit</b> override — never a silent
/// omission — and a malformed configured time fails loudly rather than coercing to a wrong deadline.
/// </summary>
public class FlattenOptionsTests
{
    private static FlattenSchedule For(FlattenOptions options, string symbol) =>
        options.ToSchedules().Single(schedule => schedule.Instrument == InstrumentId.Parse(symbol));

    [Fact]
    public void ToSchedules_ShouldArmTheKnownProducts_WhenNothingIsConfigured()
    {
        // On by default (R-13): a fresh deployment with no Flatten config is still armed for the core products.
        IReadOnlyList<FlattenSchedule> schedules = new FlattenOptions().ToSchedules();

        schedules.Select(schedule => schedule.Instrument.Symbol)
            .Should().BeEquivalentTo(["ES", "NQ", "CL", "GC"]);
        schedules.Should().OnlyContain(schedule => schedule.Enabled);
        For(new FlattenOptions(), "ES").Deadline.Should().Be(new TimeOnly(14, 30));
        For(new FlattenOptions(), "GC").Deadline.Should().Be(new TimeOnly(12, 15));
    }

    [Fact]
    public void ToSchedules_ShouldDisableAMarket_OnlyByAnExplicitOverride()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", Enabled = false }],
        };

        // ES is off because it was explicitly turned off -- and keeps its default deadline, so re-enabling needs
        // no time re-entered. The other markets stay on.
        FlattenSchedule es = For(options, "ES");
        es.Enabled.Should().BeFalse();
        es.Deadline.Should().Be(new TimeOnly(14, 30));
        For(options, "NQ").Enabled.Should().BeTrue();
    }

    [Fact]
    public void ToSchedules_ShouldApplyAConfiguredSessionClose()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "13:45" }],
        };

        For(options, "ES").Deadline.Should().Be(new TimeOnly(13, 45));
    }

    [Fact]
    public void ToSchedules_ShouldPreferADeadlineOverride_OverTheSessionClose()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = "14:30", DeadlineOverride = "14:00" }],
        };

        For(options, "ES").Deadline.Should().Be(new TimeOnly(14, 0));
    }

    [Fact]
    public void ToSchedules_ShouldAddAnInstrument_NotAmongTheDefaults()
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "YM", SessionClose = "14:30" }],
        };

        For(options, "YM").Deadline.Should().Be(new TimeOnly(14, 30));
        options.ToSchedules().Should().HaveCount(5); // the four defaults + YM
    }

    [Theory]
    [InlineData("2:30 PM")]
    [InlineData("25:00")]
    [InlineData("14-30")]
    public void ToSchedules_ShouldThrow_WhenAConfiguredTimeIsMalformed(string time)
    {
        FlattenOptions options = new()
        {
            Instruments = [new FlattenScheduleOption { Symbol = "ES", SessionClose = time }],
        };

        // A malformed deadline must fail loudly, never coerce to a wrong time on a safety path.
        Action act = () => options.ToSchedules();

        act.Should().Throw<FormatException>();
    }
}
