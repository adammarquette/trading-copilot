using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.MarketData;

/// <summary>
/// The pure key-level function (gh#626, of gh#597; R-10 / R-22) — swing pivots and the ATR-normalised zone around
/// them. A <b>domain function</b>: no clock, no store, no DI, so the same bars always produce the same zones.
/// </summary>
/// <remarks>
/// <para>
/// <b>The windows are deliberately tiny here</b> (one bar back, one forward) where production defaults are ~20/15.
/// A pivot rule is a claim about a window, and a claim nobody can check by eye is one nobody can debug — the same
/// reason <see cref="AverageTrueRangeTests"/> picks true ranges that average to round numbers. The window size is
/// an <i>input</i>, so shrinking it tests the same rule over a series that fits on one screen.
/// </para>
/// <para>
/// <b>Heikin-Ashi is the default source and that is a real choice</b>, not a detail: HA averages the bar's own
/// four prices, so a level lands in the support/resistance <i>overlap</i> rather than on a single wick a stop
/// would sit the wrong side of. The worked series below is built so the three sources give three different,
/// hand-checkable prices for the same pivot bar — which is what makes "the default matters" a testable claim.
/// </para>
/// </remarks>
public class KeyLevelsTests
{
    private static DateTimeOffset Open(int minute) => new(2026, 7, 20, 14, minute, 0, TimeSpan.Zero);

    private static Bar Bar(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(Open(minute), new Price(open), new Price(high), new Price(low), new Price(close), 100);

    /// <summary>One bar back, one forward — the same rule as production, over a series that fits on a screen.</summary>
    private static KeyLevelOptions Tiny() => KeyLevelOptions.Default with { LeftBars = 1, RightBars = 1 };

    /// <summary>
    /// A flat series with a single spike at index 2. Its three source prices are all checkable by hand:
    /// <list type="bullet">
    /// <item><description><b>HighLow</b> → the raw high, <c>120</c>.</description></item>
    /// <item><description><b>Body</b> → the raw body top, <c>max(open, close) = 110</c>.</description></item>
    /// <item><description>
    /// <b>HeikinAshiBody</b> → <c>107.5</c>. haClose = (100+120+100+110)/4 = 107.5; haOpen inherits the previous
    /// flat bar's (100+100)/2 = 100; the body top is the greater, 107.5.
    /// </description></item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<Bar> SpikeAtIndexTwo() =>
    [
        Bar(0, open: 100, high: 100, low: 100, close: 100),
        Bar(1, open: 100, high: 100, low: 100, close: 100),
        Bar(2, open: 100, high: 120, low: 100, close: 110), // the spike
        Bar(3, open: 100, high: 100, low: 100, close: 100),
        Bar(4, open: 100, high: 100, low: 100, close: 100),
    ];

    // ---- the pivot rule ----

    [Fact]
    public void FindPivots_ShouldFindAPivotHigh_WhenABarTopsItsWindow()
    {
        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(
            SpikeAtIndexTwo(), Tiny() with { Source = PivotSource.HighLow });

        pivots.Should().ContainSingle();
        pivots[0].BarIndex.Should().Be(2);
        pivots[0].Kind.Should().Be(KeyLevelKind.Resistance, "a high that holds is a ceiling");
        pivots[0].Price.Should().Be(120m);
        pivots[0].OpenTime.Should().Be(Open(2), "the pivot carries its own bar's bucket, not the window's");
    }

    [Fact]
    public void FindPivots_ShouldFindAPivotLow_Symmetrically()
    {
        IReadOnlyList<Bar> dip =
        [
            Bar(0, open: 100, high: 100, low: 100, close: 100),
            Bar(1, open: 100, high: 100, low: 100, close: 100),
            Bar(2, open: 100, high: 100, low: 80, close: 100), // the dip
            Bar(3, open: 100, high: 100, low: 100, close: 100),
            Bar(4, open: 100, high: 100, low: 100, close: 100),
        ];

        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(
            dip, Tiny() with { Source = PivotSource.HighLow });

        pivots.Should().ContainSingle();
        pivots[0].BarIndex.Should().Be(2);
        pivots[0].Kind.Should().Be(KeyLevelKind.Support, "a low that holds is a floor");
        pivots[0].Price.Should().Be(80m);
    }

    // ---- the source: the default is Heikin-Ashi, and it prices the pivot differently ----

    [Theory]
    [InlineData(PivotSource.HighLow, 120)]
    [InlineData(PivotSource.Body, 110)]
    [InlineData(PivotSource.HeikinAshiBody, 107.5)]
    public void FindPivots_ShouldPriceThePivotFromItsConfiguredSource(PivotSource source, decimal expected)
    {
        // Same bar is the pivot under all three; what changes is WHERE the level sits. A level priced off the raw
        // wick (120) sits where price never traded twice; the HA body (107.5) sits inside the overlap.
        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(SpikeAtIndexTwo(), Tiny() with { Source = source });

        pivots.Should().ContainSingle();
        pivots[0].BarIndex.Should().Be(2);
        pivots[0].Price.Should().Be(expected);
    }

    [Fact]
    public void FindPivots_ShouldReadTheHeikinAshiBody_WhenNoSourceIsChosen()
    {
        // The default is a decision, not an accident -- pin it, so a later edit to KeyLevelOptions cannot silently
        // move every level in the system onto raw wicks.
        KeyLevelOptions.Default.Source.Should().Be(PivotSource.HeikinAshiBody);

        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(SpikeAtIndexTwo(), Tiny());

        pivots.Should().ContainSingle();
        pivots[0].Price.Should().Be(107.5m);
    }

    // ---- the edges, where a pivot rule actually breaks ----

    [Fact]
    public void FindPivots_ShouldEmitNothing_WhenTheExtremeIsInsideTheUnconfirmedRightEdge()
    {
        // THE edge case the DoD names. A pivot needs RightBars of confirmation, so the newest bars cannot be
        // pivots yet however extreme -- emitting one would let the newest bar mint a level that the next bar
        // immediately invalidates.
        IReadOnlyList<Bar> risingToTheEnd =
        [
            Bar(0, open: 100, high: 100, low: 100, close: 100),
            Bar(1, open: 100, high: 100, low: 100, close: 100),
            Bar(2, open: 100, high: 100, low: 100, close: 100),
            Bar(3, open: 100, high: 100, low: 100, close: 100),
            Bar(4, open: 100, high: 200, low: 100, close: 200), // highest, but nothing confirms it
        ];

        KeyLevels.FindPivots(risingToTheEnd, Tiny() with { Source = PivotSource.HighLow })
            .Should().BeEmpty("the last bar has no right-hand window, so nothing confirms it as a pivot");
    }

    [Fact]
    public void FindPivots_ShouldEmitNothing_WhenTheExtremeIsInsideTheUnconfirmedLeftEdge()
    {
        // The lows are deliberately flat. Give bar 0 a low of 200 as well and the series grows a pivot LOW at
        // index 1 (the first bar that dips off it) -- a real pivot, but not the one under test, and it would make
        // this case pass or fail for the wrong reason.
        IReadOnlyList<Bar> openingHigh =
        [
            Bar(0, open: 200, high: 200, low: 100, close: 200), // highest, but nothing precedes it
            Bar(1, open: 100, high: 100, low: 100, close: 100),
            Bar(2, open: 100, high: 100, low: 100, close: 100),
            Bar(3, open: 100, high: 100, low: 100, close: 100),
        ];

        KeyLevels.FindPivots(openingHigh, Tiny() with { Source = PivotSource.HighLow })
            .Should().BeEmpty("the first bar has no left-hand window, so the series cannot show it topped one");
    }

    [Fact]
    public void FindPivots_ShouldEmitExactlyOne_ForAFlatPlateau()
    {
        // Three equal highs in a row. Without a tie-break this is either three pivots (three levels for one turn)
        // or none. The rule is strictly-greater on the left and greater-or-equal on the right, so the EARLIEST bar
        // of a plateau wins -- one level, deterministically, and the one where price first got there.
        IReadOnlyList<Bar> plateau =
        [
            Bar(0, open: 100, high: 100, low: 100, close: 100),
            Bar(1, open: 100, high: 150, low: 100, close: 100),
            Bar(2, open: 100, high: 150, low: 100, close: 100),
            Bar(3, open: 100, high: 150, low: 100, close: 100),
            Bar(4, open: 100, high: 100, low: 100, close: 100),
        ];

        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(
            plateau, Tiny() with { Source = PivotSource.HighLow });

        pivots.Should().ContainSingle("a plateau is one turn, not three levels");
        pivots[0].BarIndex.Should().Be(1, "the earliest bar of the plateau is where price first reached it");
    }

    // ---- total and deterministic ----

    [Fact]
    public void FindPivots_ShouldReturnNothing_WhenThereAreTooFewBarsToHoldAWindow()
    {
        IReadOnlyList<Bar> two = [Bar(0, 100, 100, 100, 100), Bar(1, 100, 110, 100, 100)];

        KeyLevels.FindPivots(two, Tiny()).Should().BeEmpty("no bar has both a left and a right window");
    }

    [Fact]
    public void FindPivots_ShouldReturnNothing_ForAnEmptySeries() =>
        KeyLevels.FindPivots([], Tiny()).Should().BeEmpty();

    [Fact]
    public void FindPivots_ShouldBeDeterministic_OverTheSameSeries()
    {
        // The DoD's own words: same bars in -> same zones out. It is what makes a rebuild restore history rather
        // than quietly rewrite it (ADR-0001), so it is asserted rather than assumed.
        IReadOnlyList<SwingPivot> first = KeyLevels.FindPivots(SpikeAtIndexTwo(), Tiny());
        IReadOnlyList<SwingPivot> second = KeyLevels.FindPivots(SpikeAtIndexTwo(), Tiny());

        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Fact]
    public void FindPivots_ShouldRefuse_WhenTheSeriesIsNotAscending()
    {
        // The same boundary discipline AverageTrueRange holds: a shuffled series does not fail on its own, it
        // quietly computes a different, wrong set of levels.
        IReadOnlyList<Bar> shuffled = [Bar(3, 100, 100, 100, 100), Bar(1, 100, 150, 100, 100), Bar(2, 100, 100, 100, 100)];

        FluentActions.Invoking(() => KeyLevels.FindPivots(shuffled, Tiny()))
            .Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void FindPivots_ShouldRefuse_WhenAWindowIsNotPositive(int left, int right)
    {
        FluentActions.Invoking(() => KeyLevels.FindPivots(
                SpikeAtIndexTwo(), KeyLevelOptions.Default with { LeftBars = left, RightBars = right }))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- the zone around a pivot ----

    /// <summary>Half the ATR, capped at 5% of price, floored at 0.25 — the three arms, all hand-checkable.</summary>
    private static KeyLevelOptions Zoning() => KeyLevelOptions.Default with
    {
        AtrMultiple = 0.5m,
        MaxHalfWidthFraction = 0.05m,
        MinHalfWidth = 0.25m,
    };

    private static SwingPivot HighAt(decimal price) => new(2, Open(2), price, KeyLevelKind.Resistance);

    [Fact]
    public void ZoneFor_ShouldUseHalfTheAtr_WhenThatIsNarrowerThanTheCap()
    {
        // ATR 4 -> half-width 2, against a cap of 100 * 5% = 5. The ATR arm wins.
        KeyLevelZone zone = KeyLevels.ZoneFor(HighAt(100m), atr: 4m, Zoning());

        zone.Top.Should().Be(102m);
        zone.Bottom.Should().Be(98m);
    }

    [Fact]
    public void ZoneFor_ShouldCapAtAFractionOfPrice_WhenTheAtrIsWide()
    {
        // ATR 40 -> half-width 20, which on a 100 price is a fifth of the instrument. The cap exists so one
        // volatile session cannot mint a "level" so wide that everything is inside it.
        KeyLevelZone zone = KeyLevels.ZoneFor(HighAt(100m), atr: 40m, Zoning());

        zone.Top.Should().Be(105m);
        zone.Bottom.Should().Be(95m);
    }

    [Fact]
    public void ZoneFor_ShouldApplyTheFloor_WhenBothArmsWouldBeVanishinglyNarrow()
    {
        // A dead-quiet session gives ATR ~ 0. Without a floor the zone collapses to a line, which nothing can
        // overlap or merge with -- every revisit would mint a new level instead of raising a touch count.
        KeyLevelZone zone = KeyLevels.ZoneFor(HighAt(100m), atr: 0.1m, Zoning());

        zone.Top.Should().Be(100.25m);
        zone.Bottom.Should().Be(99.75m);
    }

    [Fact]
    public void ZoneFor_ShouldCarryThePivotsKindAndBucket()
    {
        KeyLevels.ZoneFor(HighAt(100m), atr: 4m, Zoning()).Kind.Should().Be(KeyLevelKind.Resistance);
        KeyLevels.ZoneFor(new SwingPivot(2, Open(2), 100m, KeyLevelKind.Support), atr: 4m, Zoning())
            .Kind.Should().Be(KeyLevelKind.Support);
        KeyLevels.ZoneFor(HighAt(100m), atr: 4m, Zoning()).FormedAtBucket.Should().Be(Open(2));
    }

    [Fact]
    public void ZoneFor_ShouldKeepTheBandOrderedAndPositive_EvenAtATinyPrice()
    {
        // PriceLevel's own invariants (gh#596): Top strictly above Bottom, and Bottom positive. A zone that
        // violated either is a row the database refuses, discovered at the host rather than here.
        KeyLevelZone zone = KeyLevels.ZoneFor(HighAt(0.5m), atr: 10m, Zoning());

        zone.Top.Should().BeGreaterThan(zone.Bottom);
        zone.Bottom.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void ZoneFor_ShouldNarrowTheBand_RatherThanLetTheBottomGoNonPositive()
    {
        // The floor and the positive-bottom invariant can disagree on a very low-priced instrument: 0.20 with a
        // 0.25 floor would put the band's bottom BELOW zero. Bottom-positive is a database check (gh#596) and a
        // fact about prices; the floor is a tuning knob. The invariant wins, and the band narrows to say so.
        KeyLevelZone zone = KeyLevels.ZoneFor(HighAt(0.20m), atr: 10m, Zoning());

        zone.Bottom.Should().BeGreaterThan(0m, "a band may never reach a non-positive price");
        zone.Top.Should().BeGreaterThan(zone.Bottom);
        zone.Bottom.Should().Be(0.10m, "the half-width narrows to half the price rather than breaching the invariant");
    }

    [Fact]
    public void ZoneFor_ShouldRefuse_WhenTheAtrIsNegative() =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(100m), atr: -1m, Zoning()))
            .Should().Throw<ArgumentOutOfRangeException>();
}
