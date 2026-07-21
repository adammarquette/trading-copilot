using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Flatten;

/// <summary>
/// When the flatten fires. The deadline is a wall-clock time in the market's zone, so the arithmetic has to
/// survive daylight saving — a flatten that drifts an hour twice a year is a flatten that misses the close.
/// </summary>
public class FlattenScheduleTests
{
    private static TimeZoneInfo Central => MarketClock.CentralTime;

    /// <summary>ES: enabled, no override, 2:30 pm CT session default (pre-MOC).</summary>
    private static FlattenSchedule Es(bool enabled = true, TimeOnly? over = null)
    {
        return FlattenSchedule.Create(InstrumentId.Parse("ES"), enabled, over, new TimeOnly(14, 30));
    }

    /// <summary>A moment in Central wall-clock time, resolved to the correct offset for that date.</summary>
    private static DateTimeOffset CentralAt(int year, int month, int day, int hour, int minute)
    {
        DateTime local = new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Central.GetUtcOffset(local));
    }

    // --- Deadline resolution ---

    [Fact]
    public void Deadline_ShouldUseTheSessionDefault_WhenNoOverrideIsSet()
    {
        Es().Deadline.Should().Be(new TimeOnly(14, 30));
    }

    [Fact]
    public void Deadline_ShouldPreferTheOverride_WhenTheOperatorSetOne()
    {
        // Null means "follow the session close"; a value is a deliberate per-instrument choice (R-13).
        Es(over: new TimeOnly(12, 15)).Deadline.Should().Be(new TimeOnly(12, 15));
    }

    // --- The ordinary day ---

    [Fact]
    public void Decide_ShouldDoNothing_WhenTheDeadlineIsFarOff()
    {
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 9, 30), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.None);
        d.TimeUntilDeadline.Should().Be(TimeSpan.FromHours(5));
    }

    [Fact]
    public void Decide_ShouldWarn_WhenInsideTheWarningWindow()
    {
        // Escalating warnings precede the flatten (R-13).
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 14, 20), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Warn);
        d.TimeUntilDeadline.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Decide_ShouldNotWarn_WhenThereIsNothingOpenToFlatten()
    {
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 14, 20), hasOpenPosition: false);

        d.Action.Should().Be(FlattenAction.None);
    }

    [Fact]
    public void Decide_ShouldFlatten_AtTheDeadlineWithAnOpenPosition()
    {
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 14, 30), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Flatten);
    }

    [Fact]
    public void Decide_ShouldStillFlatten_WhenTheMomentWasMissedButTheWindowIsOpen()
    {
        // A late scheduler tick must not skip the flatten -- being ten minutes late is not a reason to leave a
        // position open into the close.
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 14, 40), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Flatten);
    }

    [Fact]
    public void Decide_ShouldDoNothing_WhenAlreadyFlatAtTheDeadline()
    {
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 14, 30), hasOpenPosition: false);

        d.Action.Should().Be(FlattenAction.None);
    }

    // --- The failure modes R-13 asks for ---

    [Fact]
    public void Decide_ShouldReportMissed_WhenTheWindowHasClosedWithAPositionStillOpen()
    {
        // Past the window with exposure still on is the degraded case ADR-0013 wants surfaced loudly, not a
        // silent no-op and not a flatten fired into the settlement window hours later.
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 18, 0), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Missed);
        d.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_ShouldReportDisabled_RatherThanSilentlyDoingNothing()
    {
        // Disabling a market is a deliberate, warned override (R-13). It must stay visible and journalable --
        // never indistinguishable from "nothing to do".
        FlattenDecision d = FlattenSchedule.Decide(
            Es(enabled: false), CentralAt(2026, 7, 20, 14, 30), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Disabled);
        d.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Decide_ShouldNeverFlatten_WhenDisabled()
    {
        foreach (int hour in new[] { 9, 14, 15, 18 })
        {
            FlattenSchedule.Decide(Es(enabled: false), CentralAt(2026, 7, 20, hour, 30), hasOpenPosition: true)
                .Action.Should().NotBe(FlattenAction.Flatten);
        }
    }

    // --- Daylight saving: the deadline is wall-clock, not a fixed UTC offset ---

    [Theory]
    [InlineData(2026, 1, 15)]  // CST (UTC-6)
    [InlineData(2026, 7, 20)]  // CDT (UTC-5)
    public void Decide_ShouldFlattenAtLocalWallClock_AcrossDaylightSaving(int year, int month, int day)
    {
        FlattenDecision d = FlattenSchedule.Decide(Es(), CentralAt(year, month, day, 14, 30), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Flatten);
    }

    [Fact]
    public void Decide_ShouldMeasureAgainstMarketTime_WhenGivenAUtcInstant()
    {
        // 19:30 UTC is 14:30 CDT in July. A scheduler ticking in UTC must land on the same decision.
        FlattenDecision d = FlattenSchedule.Decide(
            Es(), new DateTimeOffset(2026, 7, 20, 19, 30, 0, TimeSpan.Zero), hasOpenPosition: true);

        d.Action.Should().Be(FlattenAction.Flatten);
    }

    // --- Instruments that settle earlier carry their own deadline ---

    [Fact]
    public void Decide_ShouldRespectAnEarlierInstrumentDeadline_WhenGoldSettlesBeforeTheEquityClose()
    {
        // GC settles around midday, well before the equity-index close -- so the flatten is per instrument.
        FlattenSchedule gold = FlattenSchedule.Create(
            InstrumentId.Parse("GC"), enabled: true, deadlineOverride: null, sessionClose: new TimeOnly(12, 20));

        FlattenSchedule.Decide(gold, CentralAt(2026, 7, 20, 12, 20), hasOpenPosition: true)
            .Action.Should().Be(FlattenAction.Flatten);
        FlattenSchedule.Decide(Es(), CentralAt(2026, 7, 20, 12, 20), hasOpenPosition: true)
            .Action.Should().Be(FlattenAction.None);
    }
}
