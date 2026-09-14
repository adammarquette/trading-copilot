using MarqSpec.TradingCopilot.Domain.MarketData;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// How much of a blind window the clean-historical store could actually cover (gh#306, R-1, ADR-0001).
/// </summary>
/// <remarks>
/// The failure this exists to prevent is a <b>partial backfill reported as a complete one</b>. gh#227 replaced a
/// silent hole with a loud signal; a recovery that says "recovered" while having covered half the window would be
/// worse than the silence it replaced, because it would be believed.
/// </remarks>
public class GapCoverageTests
{
    private static DateTimeOffset From => new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset To => new(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);

    private static TimeSpan Minute => TimeSpan.FromMinutes(1);

    [Fact]
    public void Of_ShouldReportCompleteCoverage_WhenBarsSpanTheWholeWindow()
    {
        // Buckets every minute from 14:00 to 14:59 -- the last one closes at 15:00, so the window is covered.
        List<DateTimeOffset> buckets = [.. Enumerable.Range(0, 60).Select(minute => From.AddMinutes(minute))];

        GapCoverage coverage = GapCoverage.Of(From, To, buckets, Minute);

        coverage.IsComplete.Should().BeTrue();
        coverage.Shortfall.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Of_ShouldReportTheWholeWindowAsShortfall_WhenTheStoreHasNoBars()
    {
        // The ordinary case for an instrument nobody configured Backfill:Instruments for. Reporting "recovered"
        // here would claim a recovery that did not happen at all.
        GapCoverage coverage = GapCoverage.Of(From, To, [], Minute);

        coverage.IsComplete.Should().BeFalse();
        coverage.Shortfall.Should().Be(TimeSpan.FromHours(1));
        coverage.CoveredFrom.Should().BeNull();
        coverage.CoveredTo.Should().BeNull();
    }

    [Fact]
    public void Of_ShouldCountBothEnds_WhenCoverageStartsLateAndEndsEarly()
    {
        // Bars only for 14:10 .. 14:29 (last closes 14:30): 10 minutes missing at the front, 30 at the back.
        List<DateTimeOffset> buckets = [.. Enumerable.Range(10, 20).Select(minute => From.AddMinutes(minute))];

        GapCoverage coverage = GapCoverage.Of(From, To, buckets, Minute);

        coverage.IsComplete.Should().BeFalse();
        coverage.CoveredFrom.Should().Be(From.AddMinutes(10));
        coverage.CoveredTo.Should().Be(From.AddMinutes(30));
        coverage.Shortfall.Should().Be(TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void Of_ShouldNotCountAnInteriorHole_AndSaysSoRatherThanImplyingContiguity()
    {
        // DELIBERATE LIMITATION, pinned so it is a known bound rather than a surprise: coverage is measured from
        // the FIRST and LAST bucket, so a missing bar in the middle is not counted as shortfall. Bars arrive from
        // one periodic REST poll over a contiguous lookback window, so an interior hole means a venue omission
        // rather than the ordinary partial-coverage case this measures. If that assumption ever breaks, this test
        // is where the change lands.
        List<DateTimeOffset> withHole =
        [
            .. Enumerable.Range(0, 20).Select(minute => From.AddMinutes(minute)),
            .. Enumerable.Range(40, 20).Select(minute => From.AddMinutes(minute)),
        ];

        GapCoverage coverage = GapCoverage.Of(From, To, withHole, Minute);

        coverage.IsComplete.Should().BeTrue("the ends are covered — the 20-minute interior hole is NOT measured");
    }

    [Fact]
    public void Of_ShouldTreatAWindowThatEndsBeforeItStarts_AsNothingToCover()
    {
        // Clock skew, or a cursor committed after the oldest surviving event. An inverted window must not produce
        // a negative shortfall that reads as "over-recovered".
        GapCoverage coverage = GapCoverage.Of(To, From, [], Minute);

        coverage.IsComplete.Should().BeTrue();
        coverage.Shortfall.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Of_ShouldNotCreditCoverageBeyondTheWindow_WhenBarsExtendPastIt()
    {
        // The store legitimately holds bars either side of the window; crediting them would let a wide store
        // mask a genuinely uncovered window.
        List<DateTimeOffset> wide = [.. Enumerable.Range(-30, 150).Select(minute => From.AddMinutes(minute))];

        GapCoverage coverage = GapCoverage.Of(From, To, wide, Minute);

        coverage.IsComplete.Should().BeTrue();
        coverage.CoveredFrom.Should().Be(From, "coverage is clamped to the window, never reported wider than asked");
        coverage.CoveredTo.Should().Be(To);
    }
}
