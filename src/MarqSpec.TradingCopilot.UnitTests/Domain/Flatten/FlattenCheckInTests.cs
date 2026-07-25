using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Flatten;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Flatten;

/// <summary>
/// When the dead-man's switch may report "flat" to the outside world (gh#244, R-13, ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// The whole mechanism inverts the usual alerting direction: an external monitor pages when a check-in
/// <b>fails to arrive</b>, so <b>silence is the alarm</b>. That makes a wrongly-sent check-in far worse than a
/// missing one — it actively tells the operator everything is fine while exposure is still on, and it is the only
/// signal that survives the host dying at 14:29.
/// </para>
/// <para>
/// The two failure modes pull in opposite directions and both are covered here: checking in while exposed (a lie),
/// and failing to check in on a quiet day (a page nobody needed, which trains the operator to ignore the pager —
/// and a muted pager is worse than none).
/// </para>
/// </remarks>
public class FlattenCheckInTests
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

    // --- Before the deadline: nothing is due yet ---

    [Fact]
    public void Decide_ShouldWithhold_WhenTheDeadlineHasNotPassed()
    {
        // Flat at 2:00 pm proves nothing about 2:30 pm — a position can still be opened in between.
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 14, 0), hasOpenPosition: false, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Withhold);
    }

    // --- Past the deadline: the load-bearing cases ---

    [Fact]
    public void Decide_ShouldWithhold_WhenExposureRemainsPastTheDeadline()
    {
        // The guard the whole mechanism exists for. Checking in here would tell the operator the position was
        // closed when it is still on -- worse than no monitoring at all, because it is believed.
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: true, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Withhold);
    }

    [Fact]
    public void Decide_ShouldWithhold_WhenExposureRemainsPastTheFiringWindow()
    {
        // Deep into the Missed region (deadline + 1h): the flatten never fired and the position is still on. This
        // is precisely when the page must go out, so the check-in must stay silent.
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 15, 45), hasOpenPosition: true, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Withhold);
    }

    [Fact]
    public void Decide_ShouldSend_WhenFlatAfterTheDeadline()
    {
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: false, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Send);
    }

    [Fact]
    public void Decide_ShouldSend_WhenTheDeadlineHasJustBeenReached()
    {
        // Exactly at the deadline with nothing on: there is nothing to close, so the earliest honest check-in is
        // now. Sending early maximises the operator's remaining reaction budget -- which on CL and GC is ~15 min.
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 14, 30), hasOpenPosition: false, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Send);
    }

    [Fact]
    public void Decide_ShouldSend_WhenNoPositionWasEverOpenThatSession()
    {
        // A quiet day still checks in. Get this wrong and every flat session pages, the operator disables the
        // monitor, and the most important safety net in the system is off. Also how a holiday stays quiet without
        // needing an exchange calendar: no exposure is trivially flat.
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 11, 26, 14, 31), hasOpenPosition: false, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Send);
    }

    // --- Once per market day ---

    [Fact]
    public void Decide_ShouldReportAlreadySent_WhenAlreadyCheckedInToday()
    {
        // The host polls every 15 s; without this the monitor would take ~2400 pings between the deadline and the
        // close. Re-pinging is harmless to the monitor but pointless traffic on a safety path.
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(), CentralAt(2026, 7, 20, 14, 45), hasOpenPosition: false, lastCheckedInOn: new DateOnly(2026, 7, 20));

        d.Action.Should().Be(CheckInAction.AlreadySent);
    }

    [Fact]
    public void Decide_ShouldSend_WhenTheLastCheckInWasAPreviousDay()
    {
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: false, lastCheckedInOn: new DateOnly(2026, 7, 17));

        d.Action.Should().Be(CheckInAction.Send);
    }

    [Fact]
    public void Decide_ShouldCompareTheCheckInDayInMarketTime_WhenTheInstantIsUtc()
    {
        // 19:35 UTC on 2026-07-20 is 14:35 CT the same day. A UTC-date comparison would agree here but diverge
        // after 19:00 CT, so the day key must be the market's, like every other time in this domain.
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(),
            new DateTimeOffset(2026, 7, 20, 19, 35, 0, TimeSpan.Zero),
            hasOpenPosition: false,
            lastCheckedInOn: new DateOnly(2026, 7, 20));

        d.Action.Should().Be(CheckInAction.AlreadySent);
    }

    [Fact]
    public void Decide_ShouldUseMarketWallClock_WhenTheInstantCarriesAnotherOffset()
    {
        // Same instant as 14:35 CT, expressed in UTC. Pinning to a fixed offset would drift an hour twice a year.
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(),
            new DateTimeOffset(2026, 7, 20, 19, 35, 0, TimeSpan.Zero),
            hasOpenPosition: false,
            lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.Send);
    }

    // --- A market the operator turned off ---

    [Fact]
    public void Decide_ShouldReportNotApplicable_WhenAutoFlattenIsDisabledForTheMarket()
    {
        // Disabling a market is the operator's deliberate, warned override (R-13). The switch must not then claim
        // the market is being watched -- it reports "no check expected" so the monitor for it can be paused, rather
        // than sending an all-clear the system is not entitled to give.
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(enabled: false), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: true, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.NotApplicable);
    }

    [Fact]
    public void Decide_ShouldReportNotApplicable_WhenDisabledAndFlat()
    {
        CheckInDecision d = FlattenCheckIn.Decide(
            Es(enabled: false), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: false, lastCheckedInOn: null);

        d.Action.Should().Be(CheckInAction.NotApplicable);
    }

    // --- The reason travels with the decision ---

    [Fact]
    public void Decide_ShouldExplainItself_WhenWithholdingOnOpenExposure()
    {
        CheckInDecision d = FlattenCheckIn.Decide(Es(), CentralAt(2026, 7, 20, 14, 35), hasOpenPosition: true, lastCheckedInOn: null);

        d.Reason.Should().NotBeNullOrWhiteSpace();
        d.Reason.Should().Contain("ES");
    }
}
