using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The bar gap detector (gh#696, R-1): expected-but-absent buckets in a stored series, where "expected" is
/// keyed to the bar size AND the CME session calendar — a maintenance window or a weekend is never a gap.
/// </summary>
public class BarGapDetectorTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static readonly HashSet<DateOnly> NoHolidays = [];

    /// <summary>The CME equity-index calendar — 4:00 pm CT close, maintenance to 5:00 pm, reopen 5:00 pm.</summary>
    private static BarSessionCalendar Calendar() => new(new(16, 0), NoHolidays);

    /// <summary>An instant at a Central wall-clock time, carrying the zone's real offset for that date.</summary>
    private static DateTimeOffset Market(int month, int day, int hour, int minute = 0)
    {
        DateTime wall = new(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, MarketClock.CentralTime.GetUtcOffset(wall));
    }

    /// <summary>Every one-minute bucket start in [from, to), as the venue would align them.</summary>
    private static List<DateTimeOffset> Buckets(DateTimeOffset from, DateTimeOffset to)
    {
        List<DateTimeOffset> buckets = [];
        for (DateTimeOffset cursor = from; cursor + OneMinute <= to; cursor += OneMinute)
        {
            buckets.Add(cursor);
        }

        return buckets;
    }

    // --- The basic read: an interior hole is reported ---

    [Fact]
    public void MissingBuckets_ShouldReportAnExpectedBucketThatIsAbsent()
    {
        // Tuesday 2026-07-21, mid-session: every minute stored except 10:00.
        DateTimeOffset from = Market(7, 21, 9, 50);
        DateTimeOffset to = Market(7, 21, 10, 10);
        List<DateTimeOffset> stored = Buckets(from, to);
        stored.Remove(Market(7, 21, 10, 0));

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().Equal(Market(7, 21, 10, 0));
    }

    [Fact]
    public void MissingBuckets_ShouldBeEmpty_WhenTheSeriesIsContiguous()
    {
        DateTimeOffset from = Market(7, 21, 9, 50);
        DateTimeOffset to = Market(7, 21, 10, 10);

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(Buckets(from, to), from, to, OneMinute, Calendar());

        missing.Should().BeEmpty();
    }

    // --- Session boundaries are not gaps ---

    [Fact]
    public void MissingBuckets_ShouldNotReportTheDailyMaintenanceWindow()
    {
        // Tuesday 15:55 → 17:05 crosses the 16:00 close and the hour of maintenance. Expected buckets are
        // 15:55–15:59 (closing at or before the close) and 17:00 (the reopen) — nothing in between.
        DateTimeOffset from = Market(7, 21, 15, 55);
        DateTimeOffset to = Market(7, 21, 17, 5);
        List<DateTimeOffset> stored =
        [
            .. Buckets(Market(7, 21, 15, 55), Market(7, 21, 16, 0)),
            Market(7, 21, 17, 0),
        ];

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().BeEmpty("the 16:00–17:00 maintenance window produces no bars, so it is not a gap");
    }

    [Fact]
    public void MissingBuckets_ShouldReportTheReopenBucket_WhenItIsAbsent()
    {
        // The flip side: the first bucket after maintenance IS expected, so its absence is a real hole.
        DateTimeOffset from = Market(7, 21, 15, 55);
        DateTimeOffset to = Market(7, 21, 17, 5);
        List<DateTimeOffset> stored = Buckets(Market(7, 21, 15, 55), Market(7, 21, 16, 0));

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().Equal(Market(7, 21, 17, 0));
    }

    [Fact]
    public void MissingBuckets_ShouldNotReportTheWeekend()
    {
        // Friday 2026-07-24 15:55 → Saturday 10:00. After Friday's 15:59 bucket nothing trades until Sunday's
        // 17:00 reopen, which is outside this window — so the stored Friday tail is a COMPLETE series here.
        DateTimeOffset from = Market(7, 24, 15, 55);
        DateTimeOffset to = Market(7, 25, 10, 0);
        List<DateTimeOffset> stored = Buckets(Market(7, 24, 15, 55), Market(7, 24, 16, 0));

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().BeEmpty("no session runs from Friday's close to Sunday's reopen");
    }

    [Fact]
    public void MissingBuckets_ShouldNotReportSundayBeforeTheReopen()
    {
        // Sunday 10:00 → 18:00: only the buckets from the 17:00 reopen on are expected.
        DateTimeOffset from = Market(7, 26, 10, 0);
        DateTimeOffset to = Market(7, 26, 18, 0);
        List<DateTimeOffset> stored = Buckets(Market(7, 26, 17, 0), Market(7, 26, 18, 0));

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().BeEmpty();
    }

    [Fact]
    public void MissingBuckets_ShouldNotReportADeclaredHoliday()
    {
        // A Friday holiday: Thursday's tail stored, window crosses the whole holiday into Saturday.
        DateTimeOffset from = Market(7, 2, 15, 55);
        DateTimeOffset to = Market(7, 4, 10, 0);
        List<DateTimeOffset> stored = Buckets(Market(7, 2, 15, 55), Market(7, 2, 16, 0));
        BarSessionCalendar withHoliday = new(new(16, 0), [new(2026, 7, 3)]);

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, withHoliday);

        missing.Should().BeEmpty("the declared holiday produces no session, so its buckets are not gaps");
    }

    // --- Window discipline ---

    [Fact]
    public void MissingBuckets_ShouldIgnoreStoredBucketsOutsideTheWindow()
    {
        // A store legitimately holds bars either side of the window; only the window's expected buckets count.
        DateTimeOffset from = Market(7, 21, 10, 0);
        DateTimeOffset to = Market(7, 21, 10, 5);
        List<DateTimeOffset> stored = Buckets(Market(7, 21, 9, 0), Market(7, 21, 11, 0));
        stored.Remove(Market(7, 21, 9, 30)); // a hole OUTSIDE the window must not be reported
        stored.Remove(Market(7, 21, 10, 2)); // a hole INSIDE it must be

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, OneMinute, Calendar());

        missing.Should().Equal(Market(7, 21, 10, 2));
    }

    [Fact]
    public void MissingBuckets_ShouldNotEnumerateABucketThatCannotCloseInsideTheWindow()
    {
        // The 10:05 bucket closes at 10:06, past `to` — still forming as far as this window is concerned.
        DateTimeOffset from = Market(7, 21, 10, 0);
        DateTimeOffset to = Market(7, 21, 10, 5, 30);

        IReadOnlyList<DateTimeOffset> missing = BarGapDetector.MissingBuckets([], from, to, OneMinute, Calendar());

        missing.Should().Equal(
            Market(7, 21, 10, 0), Market(7, 21, 10, 1), Market(7, 21, 10, 2),
            Market(7, 21, 10, 3), Market(7, 21, 10, 4));
    }

    [Fact]
    public void MissingBuckets_ShouldBeEmpty_WhenTheWindowIsInvertedOrEmpty()
    {
        DateTimeOffset at = Market(7, 21, 10, 0);

        BarGapDetector.MissingBuckets([], at, at, OneMinute, Calendar()).Should().BeEmpty();
        BarGapDetector.MissingBuckets([], at, at.AddMinutes(-5), OneMinute, Calendar()).Should().BeEmpty();
    }

    // --- The bar size drives the cadence ---

    [Fact]
    public void MissingBuckets_ShouldEnumerateOnTheGivenBarSize()
    {
        // Five-minute bars: expected starts are 10:00, 10:05, … — a missing 10:05 bar is one gap, not five.
        TimeSpan fiveMinutes = TimeSpan.FromMinutes(5);
        DateTimeOffset from = Market(7, 21, 10, 0);
        DateTimeOffset to = Market(7, 21, 10, 20);
        List<DateTimeOffset> stored = [Market(7, 21, 10, 0), Market(7, 21, 10, 10), Market(7, 21, 10, 15)];

        IReadOnlyList<DateTimeOffset> missing =
            BarGapDetector.MissingBuckets(stored, from, to, fiveMinutes, Calendar());

        missing.Should().Equal(Market(7, 21, 10, 5));
    }

    [Fact]
    public void MissingBuckets_ShouldAlignToTheBarGrid_WhenTheWindowStartIsOffGrid()
    {
        // A window starting mid-bucket (10:00:30) must not invent a bucket opening there — enumeration snaps to
        // the next grid boundary.
        DateTimeOffset from = Market(7, 21, 10, 0).AddSeconds(30);
        DateTimeOffset to = Market(7, 21, 10, 3);

        IReadOnlyList<DateTimeOffset> missing = BarGapDetector.MissingBuckets([], from, to, OneMinute, Calendar());

        missing.Should().Equal(Market(7, 21, 10, 1), Market(7, 21, 10, 2));
    }
}
