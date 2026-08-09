using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The session calendar behind bar gap-detection (gh#696, R-1): which bucket starts a venue is expected to
/// produce bars for, so a session boundary, a maintenance window, or a weekend is never reported as a hole in
/// the stored series.
/// </summary>
/// <remarks>
/// The model mirrors the flatten path's session logic — <see cref="MarketClock"/> Central wall-clock time and
/// <see cref="MarketSession.MaintenanceWindow"/> — rather than inventing a second calendar: a session closes at
/// the instrument's session close, the daily maintenance window runs one hour from the close, and the next
/// session reopens at close + maintenance. CME equity index runs Sunday-evening → Friday-close, so weekend
/// buckets are non-session and Sunday-evening buckets belong to Monday's session. A declared holiday closes
/// its own session outright — but its evening reopen belongs to the next day's session and opens when that
/// day trades.
/// </remarks>
public class BarSessionCalendarTests
{
    private static TimeSpan OneMinute => TimeSpan.FromMinutes(1);

    private static TimeSpan FiveMinutes => TimeSpan.FromMinutes(5);

    /// <summary>The CME equity-index session close (wiki: market sessions) — 4:00 pm CT, reopen 5:00 pm.</summary>
    private static TimeOnly Close1600 => new(16, 0);

    private static HashSet<DateOnly> NoHolidays => [];

    /// <summary>An instant at a Central wall-clock time, carrying the zone's real offset for that date.</summary>
    private static DateTimeOffset Market(int month, int day, int hour, int minute = 0)
    {
        DateTime wall = new(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, MarketClock.CentralTime.GetUtcOffset(wall));
    }

    private static BarSessionCalendar Calendar(HashSet<DateOnly>? holidays = null) =>
        new(Close1600, holidays ?? NoHolidays);

    // --- Mid-session is expected ---

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_WhenTheBucketIsMidSession()
    {
        // Tuesday 2026-07-21 10:00 CT — deep inside the session that closes at 16:00.
        Calendar().IsExpectedSlot(Market(7, 21, 10, 0), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_WhenTheBucketClosesExactlyAtTheSessionClose()
    {
        // The 15:59 → 16:00 bucket is fully formed AT the close; the venue's includePartialBar:false returns it.
        Calendar().IsExpectedSlot(Market(7, 21, 15, 59), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_WhenMondayMorningBelongsToTheSundayEveningOpen()
    {
        // CME equity index reopens Sunday at close + maintenance; Monday's daytime bars belong to that session.
        Calendar().IsExpectedSlot(Market(7, 20, 9, 0), OneMinute).Should().BeTrue();
    }

    // --- After the maintenance window the next session is already open ---

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_WhenTheBucketIsAfterTheSameDayReopen()
    {
        // Tuesday 17:00 CT — the maintenance window ended at 17:00 and the NEXT session (closing Wednesday
        // 16:00) is open. A calendar that only knew "before the close" would report the whole evening as a gap.
        Calendar().IsExpectedSlot(Market(7, 21, 17, 0), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_WhenTheBucketIsLateEvening()
    {
        Calendar().IsExpectedSlot(Market(7, 21, 22, 30), OneMinute).Should().BeTrue();
    }

    // --- The daily maintenance window is not a gap ---

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_WhenTheBucketOpensInsideTheMaintenanceWindow()
    {
        // Tuesday 16:00 CT — the session closed at 16:00 and maintenance runs 16:00 → 17:00.
        Calendar().IsExpectedSlot(Market(7, 21, 16, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_WhenTheBucketEndsInsideTheMaintenanceWindow()
    {
        // 16:59 → 17:00 sits entirely inside maintenance even though it ends exactly at the reopen.
        Calendar().IsExpectedSlot(Market(7, 21, 16, 59), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_WhenALargeBucketStraddlesTheSessionClose()
    {
        // A 5-minute bucket opening at 15:58 would close at 16:03 — a partial bar the venue does not produce.
        Calendar().IsExpectedSlot(Market(7, 21, 15, 58), FiveMinutes).Should().BeFalse();
    }

    // --- The weekend is not a gap ---

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_OnSaturday()
    {
        Calendar().IsExpectedSlot(Market(7, 25, 10, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_OnSundayBeforeTheReopen()
    {
        // Sunday 10:00 CT is still inside the weekend gap (Friday reopen → Sunday reopen).
        Calendar().IsExpectedSlot(Market(7, 26, 10, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_WhenFridayEveningStartsTheWeekendGap()
    {
        // Friday's session closed at 16:00; from the 17:00 reopen on, nothing trades until Sunday 17:00.
        Calendar().IsExpectedSlot(Market(7, 24, 17, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_OnSundayAtTheReopen()
    {
        // Sunday 17:00 CT is the next session's open — the first expected bucket after the weekend.
        Calendar().IsExpectedSlot(Market(7, 26, 17, 0), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_WhenASundayBucketOpensOneMinuteBeforeTheReopen()
    {
        Calendar().IsExpectedSlot(Market(7, 26, 16, 59), OneMinute).Should().BeFalse();
    }

    // --- Declared holidays are non-session days outright ---

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_OnADeclaredHoliday()
    {
        HashSet<DateOnly> holidays = [new(2026, 7, 3)]; // a Friday holiday

        Calendar(holidays).IsExpectedSlot(Market(7, 3, 10, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_OnTheWeekendAfterADeclaredHoliday()
    {
        HashSet<DateOnly> holidays = [new(2026, 7, 3)]; // a Friday holiday; 2026-07-04 is a Saturday

        Calendar(holidays).IsExpectedSlot(Market(7, 4, 10, 0), OneMinute).Should().BeFalse(
            "the weekend is non-session regardless of the holiday list");
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_OnTheNextSessionDayAfterADeclaredHoliday()
    {
        HashSet<DateOnly> holidays = [new(2026, 7, 3)]; // Friday holiday; Monday 2026-07-06 trades again

        Calendar(holidays).IsExpectedSlot(Market(7, 6, 10, 0), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_ForTheSundayEveningOpenBeforeAHolidayMonday()
    {
        // When Monday is closed, the Sunday-evening session that would feed it does not open.
        HashSet<DateOnly> holidays = [new(2026, 7, 6)]; // a Monday holiday

        Calendar(holidays).IsExpectedSlot(Market(7, 5, 17, 0), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_ForTheEveningReopenOnADeclaredHoliday()
    {
        // Thanksgiving 2026-11-26 (a Thursday) is declared; CME equity index reopens 17:00 CT that evening and
        // trades into Friday 2026-11-27, a normal session. Those overnight buckets belong to FRIDAY's session —
        // a holiday suppresses its own daytime buckets, never the reopen that feeds the next trading day.
        HashSet<DateOnly> holidays = [new(2026, 11, 26)];

        Calendar(holidays).IsExpectedSlot(Market(11, 26, 17, 0), OneMinute).Should().BeTrue();
        Calendar(holidays).IsExpectedSlot(Market(11, 26, 22, 30), OneMinute).Should().BeTrue();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_ForTheHolidayDaytimeAndMaintenanceWindows()
    {
        // The same Thursday: the holiday's own session stays closed — daytime buckets, the close, and the
        // maintenance window are all non-session; only the 17:00 reopen belongs to the next session.
        HashSet<DateOnly> holidays = [new(2026, 11, 26)];

        Calendar(holidays).IsExpectedSlot(Market(11, 26, 10, 0), OneMinute).Should().BeFalse();
        Calendar(holidays).IsExpectedSlot(Market(11, 26, 15, 59), OneMinute).Should().BeFalse();
        Calendar(holidays).IsExpectedSlot(Market(11, 26, 16, 0), OneMinute).Should().BeFalse();
        Calendar(holidays).IsExpectedSlot(Market(11, 26, 16, 59), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeFalse_ForTheEveningReopen_WhenTheNextDayIsAlsoAHoliday()
    {
        // Thursday AND Friday declared (a long weekend): Thursday's reopen would feed a closed Friday, so it
        // stays shut — the same "!holidays.Contains(tomorrow)" shape the Sunday and Friday paths apply.
        HashSet<DateOnly> holidays = [new(2026, 11, 26), new(2026, 11, 27)];

        Calendar(holidays).IsExpectedSlot(Market(11, 26, 17, 0), OneMinute).Should().BeFalse();
    }

    // --- The calendar follows the instrument's own session close ---

    [Fact]
    public void IsExpectedSlot_ShouldFollowTheGivenSessionClose_WhenItDiffers()
    {
        // A product closing at 14:30 carries its maintenance 14:30 → 15:30 and its reopen at 15:30.
        BarSessionCalendar early = new(new(14, 30), NoHolidays);

        early.IsExpectedSlot(Market(7, 21, 14, 0), OneMinute).Should().BeTrue();
        early.IsExpectedSlot(Market(7, 21, 15, 0), OneMinute).Should().BeFalse();
        early.IsExpectedSlot(Market(7, 21, 15, 30), OneMinute).Should().BeTrue();
    }

    // --- Wall-clock correctness across offsets ---

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_InWinterStandardTime()
    {
        // Tuesday 2026-01-13 10:00 CST (UTC-6): the same wall-clock judgement under the other offset.
        Calendar().IsExpectedSlot(Market(1, 13, 10, 0), OneMinute).Should().BeTrue();
        Calendar().IsExpectedSlot(Market(1, 13, 16, 30), OneMinute).Should().BeFalse();
    }

    [Fact]
    public void IsExpectedSlot_ShouldBeTrue_ForTheSundaySessionAfterTheSpringForward()
    {
        // DST began Sunday 2026-03-08 02:00 CT. Sunday-evening buckets are judged in wall-clock time, so the
        // transition does not move the reopen.
        Calendar().IsExpectedSlot(Market(3, 8, 17, 0), OneMinute).Should().BeTrue();
        Calendar().IsExpectedSlot(Market(3, 8, 10, 0), OneMinute).Should().BeFalse();
    }
}
