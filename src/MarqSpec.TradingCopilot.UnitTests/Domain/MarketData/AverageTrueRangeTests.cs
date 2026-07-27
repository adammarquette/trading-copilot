using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// Average true range (gh#310, R-1) — the first indicator, and the one the execution path has been waiting on.
/// </summary>
/// <remarks>
/// <para>
/// <c>StopPlan.Create</c> has refused <c>StopProximityMetric.AverageTrueRange</c> since ADR-0007 was written,
/// because nothing could measure it: *"Refusing beats silently treating the multiple as ticks and promoting at
/// the wrong distance."* This is what lifts that refusal (gh#311 does the execution side), so the value here
/// ends up setting <b>where a live position's stop sits</b>.
/// </para>
/// <para>
/// The seed data below is chosen so every number is checkable by eye — true ranges of 10, 20, 30 average to
/// exactly 20, and the next smoothing step lands on exactly 30. An indicator nobody can verify by hand is an
/// indicator nobody can debug, and this one is load-bearing.
/// </para>
/// <para>
/// <b>Wilder's smoothing</b>, not a simple moving average of true range: that is what "ATR" means on the charts
/// the operator is reading, and a stop distance that disagrees with their chart is worse than no ATR at all.
/// </para>
/// </remarks>
public class AverageTrueRangeTests
{
    private static DateTimeOffset Open(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private static Bar Bar(int minute, decimal high, decimal low, decimal close) =>
        new(Open(minute), new Price(low), new Price(high), new Price(low), new Price(close), 100);

    /// <summary>
    /// The worked series. True ranges are 10, 20, 30, then 50 — so the first ATR (period 3) is exactly 20, and
    /// the next is exactly (20·2 + 50) / 3 = 30.
    /// </summary>
    private static IReadOnlyList<Bar> WorkedSeries() =>
    [
        Bar(0, high: 100, low: 100, close: 100), // no previous close, so no true range
        Bar(1, high: 110, low: 100, close: 105), // TR = 10
        Bar(2, high: 125, low: 105, close: 120), // TR = 20
        Bar(3, high: 150, low: 120, close: 140), // TR = 30  -> first ATR = (10+20+30)/3 = 20
        Bar(4, high: 190, low: 140, close: 180), // TR = 50  -> ATR = (20*2 + 50)/3 = 30
    ];

    // --- The arithmetic, checkable by eye ---

    [Fact]
    public void Compute_ShouldSeedFromTheMeanOfTheFirstTrueRanges()
    {
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(WorkedSeries(), period: 3);

        atr[3].Should().Be(20m);
    }

    [Fact]
    public void Compute_ShouldSmoothWilderStyle_AfterTheSeed()
    {
        // (previous * (period - 1) + trueRange) / period = (20*2 + 50) / 3 = 30.
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(WorkedSeries(), period: 3);

        atr[4].Should().Be(30m);
    }

    [Fact]
    public void Compute_ShouldCountTheGap_WhenPriceJumpsPastThePreviousClose()
    {
        // The reason ATR uses TRUE range rather than the bar's own range: a gap open is real risk the bar's
        // high-minus-low cannot see. Previous close 140, this bar 195-200 -> range 5, but true range 60.
        List<Bar> gapped = [.. WorkedSeries()];
        gapped[4] = Bar(4, high: 200, low: 195, close: 198); // prev close 140 -> TR = 200 - 140 = 60

        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(gapped, period: 3);

        // (20*2 + 60) / 3 = 33.33...; assert the true range drove it by comparing against the no-gap case.
        atr[4].Should().BeGreaterThan(30m);
    }

    // --- No value is better than a wrong one ---

    [Fact]
    public void Compute_ShouldYieldNoValue_UntilThePeriodIsSatisfied()
    {
        // A partial ATR computed over a short window would set a stop at the wrong distance while looking
        // perfectly ordinary -- exactly the silent mis-measurement StopPlan's refusal exists to prevent.
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(WorkedSeries(), period: 3);

        atr.Take(3).Should().AllSatisfy(value => value.Should().BeNull());
    }

    [Fact]
    public void Compute_ShouldYieldNothing_WhenThereAreFewerBarsThanThePeriodNeeds()
    {
        // period + 1 bars are needed: the first bar has no previous close, so it has no true range.
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute([.. WorkedSeries().Take(3)], period: 3);

        atr.Should().AllSatisfy(value => value.Should().BeNull());
    }

    [Fact]
    public void Compute_ShouldYieldNothing_WhenThereAreNoBars()
    {
        AverageTrueRange.Compute([], period: 3).Should().BeEmpty();
    }

    // --- Rebuild = replay ---

    [Fact]
    public void Compute_ShouldBeDeterministic_WhenRecomputedOverTheSameBars()
    {
        // ADR-0001's rebuild story depends on this: recomputing over the clean-historical store must reproduce
        // the same series, or a "rebuild" silently rewrites history instead of restoring it.
        IReadOnlyList<decimal?> first = AverageTrueRange.Compute(WorkedSeries(), period: 3);
        IReadOnlyList<decimal?> second = AverageTrueRange.Compute(WorkedSeries(), period: 3);

        second.Should().Equal(first);
    }

    [Fact]
    public void Compute_ShouldBeAlignedToTheInputBars()
    {
        // The caller pairs value[i] with bars[i]; a length mismatch would misdate every stored value.
        IReadOnlyList<Bar> bars = WorkedSeries();

        AverageTrueRange.Compute(bars, period: 3).Should().HaveCount(bars.Count);
    }

    // --- Refuse nonsense rather than compute it ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Compute_ShouldRejectANonPositivePeriod(int period)
    {
        Action act = () => AverageTrueRange.Compute(WorkedSeries(), period);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_ShouldRejectBarsOutOfTimeOrder()
    {
        // True range reads the PREVIOUS bar's close, so a mis-ordered series does not fail -- it quietly
        // computes a different, wrong number. Refuse it at the boundary instead.
        List<Bar> shuffled = [.. WorkedSeries()];
        (shuffled[1], shuffled[2]) = (shuffled[2], shuffled[1]);

        Action act = () => AverageTrueRange.Compute(shuffled, period: 3);

        act.Should().Throw<ArgumentException>();
    }
}
