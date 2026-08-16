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

    [Fact]
    public void FindPivots_ShouldRefuse_WhenTheSourceIsTheUnknownZero()
    {
        // The one guard here worth more than boilerplate. `PivotSource.Unknown` is the default(PivotSource), so a
        // caller who forgets to set Source gets it for free -- and if this refusal were ever weakened into a
        // fall-through-to-default, every such caller would silently read a DIFFERENT price series than they asked
        // for and get a plausible-looking set of levels off it. That is the "an enum whose zero value is the
        // permissive one is a defect" pattern the repo instructions name; the code fails closed today, and this is
        // what keeps it that way.
        KeyLevelOptions unset = Tiny() with { Source = PivotSource.Unknown };

        // Pinned by PARAMETER NAME, and that is the whole point of the assertion. Deleting the early refusal does
        // not make this call succeed -- the source-to-series switch has an exhaustive default that throws too, so a
        // bare Throw<ArgumentOutOfRangeException>() stays green with the guard gone and proves nothing. The early
        // guard names `options`; the switch default names `source`. Only the parameter name tells them apart, and
        // only the early one refuses BEFORE any bar is touched.
        FluentActions.Invoking(() => KeyLevels.FindPivots(SpikeAtIndexTwo(), unset))
            .Should().Throw<ArgumentOutOfRangeException>().WithParameterName("options");
    }

    [Fact]
    public void FindPivots_ShouldRefuse_WhenTheSeriesIsNull() =>
        FluentActions.Invoking(() => KeyLevels.FindPivots(null!, Tiny()))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void FindPivots_ShouldRefuse_WhenTheOptionsAreNull() =>
        FluentActions.Invoking(() => KeyLevels.FindPivots(SpikeAtIndexTwo(), null!))
            .Should().Throw<ArgumentNullException>();

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

    // ---- significance: how far the pivot stood out, in ATR multiples ----

    [Fact]
    public void FindPivots_ShouldMeasureProminence_AsTheDistanceBeyondTheWindowsRunnerUp()
    {
        // "How extreme the originating pivot is" (PriceLevel.Significance) has to be measured where the window
        // still exists -- by zone time it is gone. Prominence is the gap to the next-best bar in the window: the
        // spike tops 120 against neighbours at 100, so it stands out by 20.
        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(
            SpikeAtIndexTwo(), Tiny() with { Source = PivotSource.HighLow });

        pivots[0].Prominence.Should().Be(20m);
    }

    [Fact]
    public void FindPivots_ShouldMeasureProminenceSymmetrically_ForAPivotLow()
    {
        IReadOnlyList<Bar> dip =
        [
            Bar(0, open: 100, high: 100, low: 100, close: 100),
            Bar(1, open: 100, high: 100, low: 100, close: 100),
            Bar(2, open: 100, high: 100, low: 80, close: 100),
            Bar(3, open: 100, high: 100, low: 100, close: 100),
            Bar(4, open: 100, high: 100, low: 100, close: 100),
        ];

        KeyLevels.FindPivots(dip, Tiny() with { Source = PivotSource.HighLow })[0]
            .Prominence.Should().Be(20m, "a low that undercuts its window by 20 is as prominent as a high that tops it by 20");
    }

    [Fact]
    public void ZoneFor_ShouldScoreSignificance_AsProminenceInAtrMultiples()
    {
        // Normalised by ATR so the score is comparable ACROSS instruments and volatility regimes: 20 points of
        // prominence is a major level on a quiet ES session and noise on a wild one. A raw price distance would
        // rank every high-priced or high-volatility instrument above every other, which is not a ranking.
        SwingPivot pivot = new(2, Open(2), 100m, KeyLevelKind.Resistance, Prominence: 20m);

        KeyLevels.ZoneFor(pivot, atr: 4m, Zoning()).Significance.Should().Be(5m);
    }

    [Fact]
    public void ZoneFor_ShouldScoreZero_WhenThereIsNoAtrToNormaliseBy()
    {
        // Cannot measure => do not fabricate, the same posture FindPivots takes on a missing spec. A score invented
        // from a zero ATR would rank a level the detector has no volatility context for.
        SwingPivot pivot = new(2, Open(2), 100m, KeyLevelKind.Resistance, Prominence: 20m);

        KeyLevels.ZoneFor(pivot, atr: 0m, Zoning()).Significance.Should().Be(0m);
    }

    [Fact]
    public void MergeOverlapping_ShouldKeepTheStrongestSignificance()
    {
        // Touch count and significance answer different questions -- how OFTEN price respected the level, and how
        // hard it turned there. Merging takes the max rather than the sum or the mean: the level is at least as
        // strong as its strongest touch, and averaging would let a string of weak retests dilute a decisive turn.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
            [Zone(98m, 102m) with { Significance = 2m }, Zone(101m, 105m) with { Significance = 7m }]);

        merged.Should().ContainSingle();
        merged[0].Significance.Should().Be(7m);
        merged[0].TouchCount.Should().Be(2, "the counts still add -- they measure a different thing");
    }

    // ---- bounded eviction: the set cannot grow without limit ----

    [Fact]
    public void EvictAllButMostRecent_ShouldKeepTheNewestPerKind()
    {
        // Unbounded, every session's pivots accumulate forever and the oldest levels -- formed under a market that
        // no longer exists -- crowd the chart the operator actually reads.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.EvictAllButMostRecent(
            [Zone(98m, 102m, minute: 1), Zone(110m, 114m, minute: 5), Zone(120m, 124m, minute: 9)],
            maxPerKind: 2);

        kept.Should().HaveCount(2);
        kept.Select(zone => zone.FormedAtBucket).Should().BeEquivalentTo([Open(5), Open(9)]);
    }

    [Fact]
    public void EvictAllButMostRecent_ShouldBoundEachSideIndependently()
    {
        // A trending session makes highs and lows at very different rates. One shared cap would let a run of new
        // highs evict every floor beneath price -- exactly the levels that matter when it turns.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.EvictAllButMostRecent(
            [
                Zone(98m, 102m, KeyLevelKind.Resistance, minute: 1),
                Zone(110m, 114m, KeyLevelKind.Resistance, minute: 5),
                Zone(80m, 84m, KeyLevelKind.Support, minute: 2),
            ],
            maxPerKind: 1);

        kept.Should().HaveCount(2, "one per side, not one in total");
        kept.Should().ContainSingle(zone => zone.Kind == KeyLevelKind.Support);
        kept.Single(zone => zone.Kind == KeyLevelKind.Resistance).FormedAtBucket.Should().Be(Open(5));
    }

    [Fact]
    public void EvictAllButMostRecent_ShouldKeepEverything_WhenUnderTheCap() =>
        KeyLevels.EvictAllButMostRecent([Zone(98m, 102m), Zone(110m, 114m)], maxPerKind: 5)
            .Should().HaveCount(2);

    [Fact]
    public void EvictAllButMostRecent_ShouldBreakTiesOnStrength_NotArbitrarily()
    {
        // Two levels formed on the SAME bar is ordinary -- a bar can be both a pivot high and, on another
        // instrument's window, part of a cluster. Falling back to significance keeps the decision deterministic
        // AND defensible; leaving it to input order would make a rebuild drop a different level than the live pass.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.EvictAllButMostRecent(
            [
                Zone(98m, 102m, minute: 4) with { Significance = 1m },
                Zone(110m, 114m, minute: 4) with { Significance = 9m },
            ],
            maxPerKind: 1);

        kept.Should().ContainSingle();
        kept[0].Significance.Should().Be(9m);
    }

    [Fact]
    public void EvictAllButMostRecent_ShouldRefuse_WhenTheCapIsNotPositive() =>
        FluentActions.Invoking(() => KeyLevels.EvictAllButMostRecent([Zone(98m, 102m)], maxPerKind: 0))
            .Should().Throw<ArgumentOutOfRangeException>();

    // ---- merging overlapping zones: one level, counted twice ----

    private static KeyLevelZone Zone(
        decimal bottom, decimal top, KeyLevelKind kind = KeyLevelKind.Resistance, int minute = 2, int touches = 1) =>
        new(bottom, top, kind, Open(minute), touches);

    [Fact]
    public void MergeOverlapping_ShouldFoldTwoOverlappingZonesIntoOne_RaisingTheTouchCount()
    {
        // A price revisited is a STRONGER level, not a second one. Two zones left side by side would each look
        // half as significant as the single level they actually describe.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping([Zone(98m, 102m), Zone(101m, 105m)]);

        merged.Should().ContainSingle();
        merged[0].Bottom.Should().Be(98m);
        merged[0].Top.Should().Be(105m, "the band spans both touches -- price respected the whole of it");
        merged[0].TouchCount.Should().Be(2);
    }

    [Fact]
    public void MergeOverlapping_ShouldLeaveDisjointZonesAlone()
    {
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping([Zone(98m, 102m), Zone(110m, 114m)]);

        merged.Should().HaveCount(2);
        merged.Select(zone => zone.TouchCount).Should().AllBeEquivalentTo(1);
    }

    [Fact]
    public void MergeOverlapping_ShouldCollapseThree_WhenOneZoneBridgesTwoNeighbours()
    {
        // THE case the DoD names. Two disjoint zones plus a third that overlaps BOTH is one level, not two: a
        // single pass that merges pairwise and stops would leave the bridge folded into whichever neighbour it
        // met first and the other stranded, so the same price would carry two levels of half the strength.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
            [Zone(98m, 102m), Zone(108m, 112m), Zone(101m, 109m)]);

        merged.Should().ContainSingle("the bridging zone makes all three one level");
        merged[0].Bottom.Should().Be(98m);
        merged[0].Top.Should().Be(112m);
        merged[0].TouchCount.Should().Be(3);
    }

    [Fact]
    public void MergeOverlapping_ShouldMergeZonesThatMerelyTouch()
    {
        // Contiguous bands are one band: a gap of exactly nothing is not a gap, and leaving them apart would put a
        // seam at a price the operator sees as a single level.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping([Zone(98m, 102m), Zone(102m, 106m)]);

        merged.Should().ContainSingle();
        merged[0].TouchCount.Should().Be(2);
    }

    [Fact]
    public void MergeOverlapping_ShouldNeverMergeAcrossKinds()
    {
        // A floor and a ceiling at the same price are two different claims about what price does there -- one says
        // it holds above, the other below. Folding them would have to pick a side, and a level that silently
        // changed which way it faces is worse than two honest ones. Role REVERSAL is the sanctioned way a zone
        // changes side, and it is a close through the band, not an overlap.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
            [Zone(98m, 102m, KeyLevelKind.Support), Zone(99m, 103m, KeyLevelKind.Resistance)]);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void MergeOverlapping_ShouldNotShrinkALevel_WhenAZoneFallsWhollyInsideIt()
    {
        // A tight touch inside a wide level must STRENGTHEN it, not narrow it. Taking the newcomer's top instead of
        // the union would quietly shave the band every time price retested it near the middle -- the level would
        // creep tighter with each confirmation, which is backwards.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping([Zone(98m, 110m), Zone(101m, 105m)]);

        merged.Should().ContainSingle();
        merged[0].Bottom.Should().Be(98m);
        merged[0].Top.Should().Be(110m, "the wider band stands; a contained touch cannot shrink it");
        merged[0].TouchCount.Should().Be(2);
    }

    [Fact]
    public void MergeOverlapping_ShouldKeepTheEarliestFormation()
    {
        // The level dates from when price FIRST respected it; a later touch strengthens it without resetting its age.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
            [Zone(101m, 105m, minute: 9), Zone(98m, 102m, minute: 3)]);

        merged.Should().ContainSingle();
        merged[0].FormedAtBucket.Should().Be(Open(3));
    }

    [Fact]
    public void MergeOverlapping_ShouldNotDependOnInputOrder()
    {
        // Same bars in -> same zones out (the DoD). Pivots arrive in bar order, but a merge that depended on it
        // would make a rebuild produce a different chart from the live pass.
        KeyLevelZone[] zones = [Zone(98m, 102m), Zone(108m, 112m), Zone(101m, 109m)];

        KeyLevels.MergeOverlapping(zones.Reverse().ToList())
            .Should().BeEquivalentTo(KeyLevels.MergeOverlapping(zones), options => options.WithStrictOrdering());
    }

    [Fact]
    public void MergeOverlapping_ShouldReturnNothing_ForAnEmptySet() =>
        KeyLevels.MergeOverlapping([]).Should().BeEmpty();

    // ---- role reversal: broken support becomes resistance ----

    [Fact]
    public void ApplyClose_ShouldFlipSupportToResistance_WhenPriceClosesBelowIt()
    {
        // The classic reversal: a floor that gave way is a ceiling on the way back up. Leaving it Support would
        // have the operator reading a floor under a price that has already fallen through it.
        IReadOnlyList<KeyLevelZone> after = KeyLevels.ApplyClose([Zone(98m, 102m, KeyLevelKind.Support)], close: 97m);

        after.Should().ContainSingle();
        after[0].Kind.Should().Be(KeyLevelKind.Resistance);
        after[0].Bottom.Should().Be(98m, "a reversal changes which way the level faces, never where it sits");
        after[0].Top.Should().Be(102m);
    }

    [Fact]
    public void ApplyClose_ShouldFlipResistanceToSupport_WhenPriceClosesAboveIt()
    {
        IReadOnlyList<KeyLevelZone> after = KeyLevels.ApplyClose([Zone(98m, 102m, KeyLevelKind.Resistance)], close: 103m);

        after[0].Kind.Should().Be(KeyLevelKind.Support);
    }

    [Fact]
    public void ApplyClose_ShouldLeaveTheZoneAlone_WhenTheCloseIsInsideTheBand()
    {
        // Price trading INSIDE the zone has not broken it -- that is the level being tested, which is the ordinary
        // case and the whole reason a zone is a band rather than a line.
        KeyLevels.ApplyClose([Zone(98m, 102m, KeyLevelKind.Support)], close: 100m)[0]
            .Kind.Should().Be(KeyLevelKind.Support);
    }

    [Theory]
    [InlineData(98)]
    [InlineData(102)]
    public void ApplyClose_ShouldNotFlip_WhenTheCloseSitsExactlyOnABoundary(decimal close)
    {
        // THE boundary case the DoD names. A close AT the edge is at the level, not through it -- the band is
        // inclusive of its own bounds. Using a non-strict comparison here would flip a level every time price
        // merely touched its edge, which is the most common thing price does at a level it respects.
        KeyLevels.ApplyClose([Zone(98m, 102m, KeyLevelKind.Support)], close)[0]
            .Kind.Should().Be(KeyLevelKind.Support);
    }

    [Fact]
    public void ApplyClose_ShouldReFlip_WhenPriceClosesBackThroughTheOtherSide()
    {
        // THE re-flip the DoD names. A whipsaw through a level and back is common, and the second flip has to be
        // as available as the first -- a one-shot reversal would leave the level permanently facing the wrong way
        // after a single fake-out.
        IReadOnlyList<KeyLevelZone> broken = KeyLevels.ApplyClose([Zone(98m, 102m, KeyLevelKind.Support)], close: 97m);
        broken[0].Kind.Should().Be(KeyLevelKind.Resistance);

        IReadOnlyList<KeyLevelZone> reclaimed = KeyLevels.ApplyClose(broken, close: 103m);
        reclaimed[0].Kind.Should().Be(KeyLevelKind.Support, "price reclaimed the level, so it faces the other way again");
    }

    [Fact]
    public void ApplyClose_ShouldPreserveEverythingButTheKind()
    {
        KeyLevelZone before = Zone(98m, 102m, KeyLevelKind.Support, minute: 7, touches: 4);

        KeyLevelZone after = KeyLevels.ApplyClose([before], close: 97m)[0];

        after.Should().BeEquivalentTo(before with { Kind = KeyLevelKind.Resistance });
    }

    [Fact]
    public void ZoneFor_ShouldRefuse_WhenTheAtrIsNegative() =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(100m), atr: -1m, Zoning()))
            .Should().Throw<ArgumentOutOfRangeException>();

    // The three width arms are business-rule invariants, not boilerplate: each one, left unguarded, produces a
    // zone that is silently WRONG rather than absent. A non-positive AtrMultiple or MinHalfWidth collapses the
    // band to a line or inverts it (Bottom above Top), so nothing can ever overlap it and every level reads as
    // untouched; a MaxHalfWidthFraction above 1 lets the cap exceed price itself, minting a zone that swallows the
    // whole chart and swallows every close with it. Both failures look like "no levels formed", which is exactly
    // what a quiet session looks like -- so nothing downstream would flag them.
    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void ZoneFor_ShouldRefuse_WhenTheAtrMultipleIsNotPositive(decimal multiple) =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(100m), atr: 4m, Zoning() with { AtrMultiple = multiple }))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0)]
    [InlineData(-0.25)]
    public void ZoneFor_ShouldRefuse_WhenTheMinimumHalfWidthIsNotPositive(decimal floor) =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(100m), atr: 4m, Zoning() with { MinHalfWidth = floor }))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0)]
    [InlineData(-0.05)]
    [InlineData(1.5)]
    public void ZoneFor_ShouldRefuse_WhenTheMaximumHalfWidthFractionIsOutOfRange(decimal fraction) =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(
                HighAt(100m), atr: 4m, Zoning() with { MaxHalfWidthFraction = fraction }))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void ZoneFor_ShouldRefuse_WhenThePivotPriceIsNotPositive(decimal price) =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(price), atr: 4m, Zoning()))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void ZoneFor_ShouldRefuse_WhenThePivotIsNull() =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(null!, atr: 4m, Zoning()))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void ZoneFor_ShouldRefuse_WhenTheOptionsAreNull() =>
        FluentActions.Invoking(() => KeyLevels.ZoneFor(HighAt(100m), atr: 4m, null!))
            .Should().Throw<ArgumentNullException>();

    // ---- the remaining null guards ----

    // All three pin the PARAMETER NAME, for the same reason the PivotSource.Unknown case does: a bare
    // Throw<ArgumentNullException>() cannot fail here. Each method hands its collection straight to LINQ
    // (OrderBy / Select / GroupBy), and LINQ null-checks its own `source` argument -- so the call throws the right
    // EXCEPTION TYPE whether or not this file guards anything, and the test would stay green over a deleted guard.
    // The explicit guard names `zones`; LINQ names `source`. Asserting the name is what makes these tests able to
    // fail, and what pins the guard's real value: refusing at the boundary, named for the caller's own argument,
    // rather than surfacing from inside a chain the caller never wrote.
    [Fact]
    public void MergeOverlapping_ShouldRefuse_WhenTheZonesAreNull() =>
        FluentActions.Invoking(() => KeyLevels.MergeOverlapping(null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("zones");

    [Fact]
    public void ApplyClose_ShouldRefuse_WhenTheZonesAreNull() =>
        FluentActions.Invoking(() => KeyLevels.ApplyClose(null!, close: 100m))
            .Should().Throw<ArgumentNullException>().WithParameterName("zones");

    [Fact]
    public void EvictAllButMostRecent_ShouldRefuse_WhenTheZonesAreNull() =>
        FluentActions.Invoking(() => KeyLevels.EvictAllButMostRecent(null!, maxPerKind: 1))
            .Should().Throw<ArgumentNullException>().WithParameterName("zones");

    // ---- Detect: the whole pipeline, merge -> flip -> evict, as one pure function ----

    /// <summary>The tiny 1/1 window on the raw high/low series, so every zone below is hand-checkable.</summary>
    private static KeyLevelOptions HighLowTiny() => Tiny() with { Source = PivotSource.HighLow };

    /// <summary>Two isolated pivot highs — 120 at minute 2, 140 at minute 6 — over a flat series.</summary>
    private static IReadOnlyList<Bar> TwoHighs120At2And140At6() =>
    [
        Bar(0, 100, 100, 100, 100),
        Bar(1, 100, 100, 100, 100),
        Bar(2, 100, 120, 100, 100),
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 100, 100, 100),
        Bar(5, 100, 100, 100, 100),
        Bar(6, 100, 140, 100, 100),
        Bar(7, 100, 100, 100, 100),
        Bar(8, 100, 100, 100, 100),
    ];

    [Fact]
    public void Detect_ShouldMeasureEachPivotAgainstItsOwnBucketsAtr()
    {
        // The ATR that sizes a zone is read at the PIVOT'S OWN bucket, not one shared reading -- so two pivots on
        // bars of different volatility get differently-wide zones. min-2 is measured at ATR 4 (half-width 2), and
        // min-6 at ATR 10 (half-width 5). A single shared ATR would give them the same width and hide the bug.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m, [Open(6)] = 10m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            TwoHighs120At2And140At6(), atr, HighLowTiny(), maxPerKind: 12);

        KeyLevelZone atTwo = zones.Single(zone => zone.FormedAtBucket == Open(2));
        KeyLevelZone atSix = zones.Single(zone => zone.FormedAtBucket == Open(6));
        atTwo.Bottom.Should().Be(118m);
        atTwo.Top.Should().Be(122m);
        atSix.Bottom.Should().Be(135m);
        atSix.Top.Should().Be(145m);
    }

    [Fact]
    public void Detect_ShouldSkipAPivot_WhenNoAtrExistsAtItsBucket()
    {
        // "Cannot measure => do not fabricate", the same posture ZoneFor takes on a zero ATR: a pivot whose bucket
        // has no ATR yields NO zone rather than one off a made-up width. Only min-2 has an ATR here, so only it forms.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            TwoHighs120At2And140At6(), atr, HighLowTiny(), maxPerKind: 12);

        zones.Should().ContainSingle();
        zones[0].FormedAtBucket.Should().Be(Open(2));
    }

    /// <summary>
    /// A pivot high at minute 2, then minute 4 — the last bar — closes at 200 straight through its zone. The
    /// close-through is deliberately the final bar: a trailing flat bar at 100 would break the freshly-flipped
    /// support from below and flip it back, which is a fact about the series, not about <see cref="KeyLevels.Detect"/>.
    /// </summary>
    private static IReadOnlyList<Bar> HighAt2ThenCloseThroughAt4() =>
    [
        Bar(0, 100, 100, 100, 100),
        Bar(1, 100, 100, 100, 100),
        Bar(2, 100, 120, 100, 100), // pivot high 120 -> resistance
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 200, 100, 200), // last bar, closes 200 through the zone; on the un-windowed edge, so not a pivot
    ];

    [Fact]
    public void Detect_ShouldFlipAZone_WhenALaterCloseBreaksThroughIt()
    {
        // Role reversal: a resistance the price later closes above becomes support. min-2's ceiling [118,122] is
        // broken by min-4's close of 200 and flips. min-4 sits on the un-windowed edge, so it is not a pivot and
        // the only zone is min-2's, now flipped to support.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            HighAt2ThenCloseThroughAt4(), atr, HighLowTiny(), maxPerKind: 12);

        zones.Should().ContainSingle();
        zones[0].FormedAtBucket.Should().Be(Open(2));
        zones[0].Kind.Should().Be(KeyLevelKind.Support, "price closed above the resistance, reversing its role");
    }

    /// <summary>Minute 1 closes at 200, THEN a pivot high forms at minute 4 — the close predates the level.</summary>
    private static IReadOnlyList<Bar> CloseThroughAt1ThenHighAt4() =>
    [
        Bar(0, 100, 200, 100, 100), // high 200 sits on the un-windowed left edge, so it is not itself a pivot
        Bar(1, 100, 200, 100, 200), // closes 200, but its high equals bar 0's, so it is not a pivot either
        Bar(2, 100, 100, 100, 100),
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 120, 100, 100), // pivot high 120 -> resistance, formed AFTER the 200-close
        Bar(5, 100, 100, 100, 100),
        Bar(6, 100, 100, 100, 100),
    ];

    [Fact]
    public void Detect_ShouldNotFlipAZone_WhenTheOnlyCloseThroughPrecedesItsFormation()
    {
        // Formation order is respected. min-1 closes at 200, but the min-4 resistance had not formed yet, so that
        // close cannot reach back and reverse it. A pipeline replaying every close regardless of order would flip
        // this to support; it must stay resistance. This pins the "FormedAtBucket < bar" guard in the replay.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(4)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            CloseThroughAt1ThenHighAt4(), atr, HighLowTiny(), maxPerKind: 12);

        zones.Should().ContainSingle();
        zones[0].FormedAtBucket.Should().Be(Open(4));
        zones[0].Kind.Should().Be(KeyLevelKind.Resistance, "a close predating a zone's formation cannot flip it");
    }

    /// <summary>Two pivot highs one tick apart — 120 at minute 2, 121 at minute 4 — whose zones overlap.</summary>
    private static IReadOnlyList<Bar> TwoOverlappingHighsAt2And4() =>
    [
        Bar(0, 100, 100, 100, 100),
        Bar(1, 100, 100, 100, 100),
        Bar(2, 100, 120, 100, 100),
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 121, 100, 100),
        Bar(5, 100, 100, 100, 100),
        Bar(6, 100, 100, 100, 100),
    ];

    [Fact]
    public void Detect_ShouldRaiseTouchCount_WhenTwoPivotsOverlapThroughThePipeline()
    {
        // Two nearby pivots describe ONE stronger level, not two: their zones overlap and merge, the union spans
        // both, the touch count rides all the way through the pipeline, and the level dates from the earlier pivot.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m, [Open(4)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            TwoOverlappingHighsAt2And4(), atr, HighLowTiny(), maxPerKind: 12);

        zones.Should().ContainSingle();
        zones[0].TouchCount.Should().Be(2);
        zones[0].Bottom.Should().Be(118m);
        zones[0].Top.Should().Be(123m);
        zones[0].FormedAtBucket.Should().Be(Open(2));
    }

    /// <summary>Three disjoint pivot highs — 120, 130, 140 at minutes 2, 4, 6.</summary>
    private static IReadOnlyList<Bar> ThreeDisjointHighsAt2And4And6() =>
    [
        Bar(0, 100, 100, 100, 100),
        Bar(1, 100, 100, 100, 100),
        Bar(2, 100, 120, 100, 100),
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 130, 100, 100),
        Bar(5, 100, 100, 100, 100),
        Bar(6, 100, 140, 100, 100),
        Bar(7, 100, 100, 100, 100),
        Bar(8, 100, 100, 100, 100),
    ];

    [Fact]
    public void Detect_ShouldKeepAtMostMaxPerKind_NewestFirst()
    {
        // The live set is bounded per side: three resistances, a cap of two, keeps the two most recently formed.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m, [Open(4)] = 4m, [Open(6)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            ThreeDisjointHighsAt2And4And6(), atr, HighLowTiny(), maxPerKind: 2);

        zones.Should().HaveCount(2);
        zones.Select(zone => zone.FormedAtBucket).Should().BeEquivalentTo(new[] { Open(4), Open(6) });
    }

    [Fact]
    public void Detect_ShouldBeDeterministic_OverTheSameInputs()
    {
        // The DoD's own words: same inputs in -> same zones out, so a rebuild restores history rather than
        // rewriting it (ADR-0001). Asserted with strict ordering, since the result's order is part of the contract.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m, [Open(6)] = 10m };
        IReadOnlyList<Bar> bars = TwoHighs120At2And140At6();

        IReadOnlyList<KeyLevelZone> first = KeyLevels.Detect(bars, atr, HighLowTiny(), maxPerKind: 12);
        IReadOnlyList<KeyLevelZone> second = KeyLevels.Detect(bars, atr, HighLowTiny(), maxPerKind: 12);

        second.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// A pivot low at minute 2 (support) and a pivot high at minute 6 (resistance) whose bands touch, then minute 9
    /// — the last bar — closes at 200 and flips the resistance.
    /// </summary>
    private static IReadOnlyList<Bar> SupportAt2AndResistanceAt6ThenFlipAt9() =>
    [
        Bar(0, 100, 100, 100, 100),
        Bar(1, 100, 100, 100, 100),
        Bar(2, 100, 100, 98, 100),  // dip -> pivot LOW 98 -> support [96,100]
        Bar(3, 100, 100, 100, 100),
        Bar(4, 100, 100, 100, 100),
        Bar(5, 100, 100, 100, 100),
        Bar(6, 100, 102, 100, 100), // spike -> pivot HIGH 102 -> resistance [100,104]
        Bar(7, 100, 100, 100, 100),
        Bar(8, 100, 100, 100, 100),
        Bar(9, 100, 200, 100, 200), // last bar closes 200, flipping the resistance; nothing merges afterwards
    ];

    [Fact]
    public void Detect_ShouldNotReMergeAfterAFlip_LeavingTwoOverlappingSameKindZones()
    {
        // Pipeline order is merge -> flip -> evict, with NO second merge. A support and a resistance that only
        // touch are two levels (a merge never crosses kinds); when a later close flips the resistance to support
        // they become two SAME-kind zones that overlap -- and they must stay two. Re-merging here would fold a role
        // reversal into a neighbour it never touched and silently halve two levels into one.
        Dictionary<DateTimeOffset, decimal> atr = new() { [Open(2)] = 4m, [Open(6)] = 4m };

        IReadOnlyList<KeyLevelZone> zones = KeyLevels.Detect(
            SupportAt2AndResistanceAt6ThenFlipAt9(), atr, HighLowTiny(), maxPerKind: 12);

        zones.Should().HaveCount(2);
        zones.Should().OnlyContain(zone => zone.Kind == KeyLevelKind.Support);
        zones.Should().OnlyContain(zone => zone.TouchCount == 1, "a re-merge would have summed the touch counts");
        zones.Select(zone => zone.Bottom).Should().BeEquivalentTo(new[] { 96m, 100m });
        zones.Select(zone => zone.Top).Should().BeEquivalentTo(new[] { 100m, 104m });
    }

    [Fact]
    public void Detect_ShouldReturnNothing_ForAnEmptySeries() =>
        KeyLevels.Detect([], new Dictionary<DateTimeOffset, decimal>(), HighLowTiny(), maxPerKind: 12)
            .Should().BeEmpty();

    [Fact]
    public void Detect_ShouldRefuse_WhenBarsAreNull() =>
        FluentActions.Invoking(() => KeyLevels.Detect(
                null!, new Dictionary<DateTimeOffset, decimal>(), HighLowTiny(), maxPerKind: 12))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Detect_ShouldRefuse_WhenTheAtrMapIsNull() =>
        FluentActions.Invoking(() => KeyLevels.Detect(
                TwoHighs120At2And140At6(), null!, HighLowTiny(), maxPerKind: 12))
            .Should().Throw<ArgumentNullException>().WithParameterName("atrByBucket");

    [Fact]
    public void Detect_ShouldRefuse_WhenOptionsAreNull() =>
        FluentActions.Invoking(() => KeyLevels.Detect(
                TwoHighs120At2And140At6(), new Dictionary<DateTimeOffset, decimal>(), null!, maxPerKind: 12))
            .Should().Throw<ArgumentNullException>();
}
