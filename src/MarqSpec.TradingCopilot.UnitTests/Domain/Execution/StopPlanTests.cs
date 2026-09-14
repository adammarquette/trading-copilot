using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Execution;

/// <summary>
/// The staged-stop plan (ADR-0007, gh#11): the <b>actual</b> stop is held hidden while price is far and is
/// promoted to a native working order once price comes within a configured proximity; the <b>safety</b> stop
/// rests beyond it, always native, as catastrophic insurance. The invariant this type exists to hold is
/// <b>safety-beyond-actual</b> — if the safety stop were nearer, it would trigger first and the model inverts.
/// </summary>
public class StopPlanTests
{
    private static InstrumentSpec Mes => InstrumentSpec.Create(InstrumentId.Parse("MES"), 0.25m, 5m);

    /// <summary>
    /// The band the default plans' 4-tick proximity resolves to (4 × 0.25). Since gh#311 the <i>caller</i>
    /// resolves the metric and hands <see cref="StopPlan.ShouldPromote"/> an absolute distance, so these tests
    /// pass the resolved number the same way the promotion watcher does.
    /// </summary>
    private const decimal Band = 1.00m;

    private static StopPlan BuyPlan(
        decimal entry = 5_300m,
        decimal actualStop = 5_295m,
        decimal safetyStop = 5_290m,
        StopProximity? proximity = null)
    {
        return StopPlan.Create(
            Mes, OrderSide.Buy, new Price(entry), new Price(actualStop), new Price(safetyStop),
            proximity ?? StopProximity.Ticks(4));
    }

    private static StopPlan SellPlan(
        decimal entry = 5_300m,
        decimal actualStop = 5_305m,
        decimal safetyStop = 5_310m,
        StopProximity? proximity = null)
    {
        return StopPlan.Create(
            Mes, OrderSide.Sell, new Price(entry), new Price(actualStop), new Price(safetyStop),
            proximity ?? StopProximity.Ticks(4));
    }

    // --- Construction invariants ---

    [Fact]
    public void Create_ShouldStartHidden_WithBothStopsRecorded()
    {
        StopPlan plan = BuyPlan();

        // The actual stop starts synthetic: hiding it is the point (no order anticipation). The safety stop is
        // native from placement -- inc 3 transmits it as the entry's protective bracket.
        plan.Staging.Should().Be(StopStaging.Hidden);
        plan.ActualStop.Should().Be(new Price(5_295m));
        plan.SafetyStop.Should().Be(new Price(5_290m));
    }

    [Theory]
    [InlineData(5_296)] // safety INSIDE the actual stop -- would trigger first
    [InlineData(5_295)] // equal -- no insurance margin at all
    public void Create_ShouldRefuse_WhenTheSafetyStopIsNotBeyondTheActualStop_OnABuy(decimal safetyStop)
    {
        // THE invariant: catastrophic insurance must sit further from entry than the working stop. A safety
        // stop at or inside the actual stop fires first, so the "deterministic worst case" is neither.
        Func<StopPlan> act = () => BuyPlan(safetyStop: safetyStop);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(5_304)]
    [InlineData(5_305)]
    public void Create_ShouldRefuse_WhenTheSafetyStopIsNotBeyondTheActualStop_OnASell(decimal safetyStop)
    {
        Func<StopPlan> act = () => SellPlan(safetyStop: safetyStop);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(5_301)] // stop above entry on a buy -- not a stop at all
    [InlineData(5_300)] // stop AT entry -- zero risk distance, unsizeable
    public void Create_ShouldRefuse_AnActualStopOnTheWrongSideOfEntry_OnABuy(decimal actualStop)
    {
        Func<StopPlan> act = () => BuyPlan(actualStop: actualStop, safetyStop: actualStop - 5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- Proximity: the promotion decision ---

    [Fact]
    public void ShouldPromote_ShouldBeFalse_WhilePriceIsFarFromTheActualStop()
    {
        StopPlan plan = BuyPlan(); // actual 5295, band 1.00 -> promote at <= 5296.00

        plan.ShouldPromote(new Price(5_299m), Band).Should().BeFalse();
        plan.ShouldPromote(new Price(5_296.25m), Band).Should().BeFalse();
    }

    [Fact]
    public void ShouldPromote_ShouldBeTrue_OnceWithinTheProximityBand_OnABuy()
    {
        StopPlan plan = BuyPlan();

        plan.ShouldPromote(new Price(5_296m), Band).Should().BeTrue();    // exactly at the band edge
        plan.ShouldPromote(new Price(5_295.5m), Band).Should().BeTrue();
        plan.ShouldPromote(new Price(5_294m), Band).Should().BeTrue();    // already through the stop
    }

    [Fact]
    public void ShouldPromote_ShouldMirrorTheBand_OnASell()
    {
        StopPlan plan = SellPlan(); // actual 5305, band 1.00 -> promote at >= 5304.00

        plan.ShouldPromote(new Price(5_301m), Band).Should().BeFalse();
        plan.ShouldPromote(new Price(5_304m), Band).Should().BeTrue();
        plan.ShouldPromote(new Price(5_306m), Band).Should().BeTrue();
    }

    [Fact]
    public void ShouldPromote_ShouldRefuseANegativeBand()
    {
        // Unreachable by construction -- every metric resolves to a non-negative distance -- so reaching here is a
        // logic error, and a NEGATIVE band silently delays promotion past the stop rather than at it. Refuse it
        // loudly instead of quietly protecting later than the plan says.
        Func<bool> act = () => BuyPlan().ShouldPromote(new Price(5_294m), -0.25m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Promote_ShouldMoveTheStagingToNative_AndBeIdempotent()
    {
        StopPlan plan = BuyPlan();

        StopPlan promoted = plan.Promote();
        promoted.Staging.Should().Be(StopStaging.Native);

        // Promotion is one-way: a hidden stop becomes native and never silently reverts. Re-promoting is a
        // harmless no-op so a retrying watcher cannot corrupt the plan.
        promoted.Promote().Staging.Should().Be(StopStaging.Native);
    }

    [Fact]
    public void ShouldPromote_ShouldBeFalse_OnceAlreadyNative()
    {
        StopPlan plan = BuyPlan().Promote();

        // Nothing left to promote -- the watcher must not re-transmit an order that already rests at the venue.
        plan.ShouldPromote(new Price(5_294m), Band).Should().BeFalse();
    }

    // --- Proximity construction ---

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Ticks_ShouldRefuseANonPositiveBand(int ticks)
    {
        Func<StopProximity> act = () => StopProximity.Ticks(ticks);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.01)] // more than the whole entry->stop distance is not a proximity band
    public void DistanceFraction_ShouldRefuseAFractionOutsideZeroToOne(decimal fraction)
    {
        Func<StopProximity> act = () => StopProximity.DistanceFraction(fraction);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void AverageTrueRange_ShouldRefuseANonPositiveMultiple(decimal multiple)
    {
        Func<StopProximity> act = () => StopProximity.AverageTrueRange(multiple);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_ShouldAcceptAnAverageTrueRangeBand_NowThatTheIndicatorPipelineExists()
    {
        // FLIPPED PIN (gh#311). This asserted a NotSupportedException while no indicator pipeline could measure
        // ATR (R-22). gh#310 landed the projection and resolution moved to the caller, so the domain no longer
        // refuses the metric -- an unmeasurable band is now refused where it is RESOLVED, not where it is built.
        StopPlan plan = BuyPlan(proximity: StopProximity.AverageTrueRange(2m));

        plan.Proximity.Metric.Should().Be(StopProximityMetric.AverageTrueRange);
        plan.Staging.Should().Be(StopStaging.Hidden);
    }

    // --- Proximity resolution: metric -> an absolute distance (gh#311) ---

    [Fact]
    public void ResolveDistance_ShouldMeasureTicks_InTheInstrumentsOwnUnits()
    {
        StopProximity.Ticks(4)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_295m), averageTrueRange: null)
            .Should().Be(1.00m); // 4 x 0.25
    }

    [Fact]
    public void ResolveDistance_ShouldMeasureAFractionOfTheEntryToStopDistance_NotOfRawPrice()
    {
        // ADR-0007 is explicit: proximity is ticks / ATR / a fraction of the entry->stop distance -- NEVER a
        // percentage of raw price, which would scale with the instrument's absolute level rather than the risk.
        // At 5300 a 0.2% -of-price band would be 10.60; the correct answer is 20% of the 10-point risk = 2.00.
        StopProximity.DistanceFraction(0.2m)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_290m), averageTrueRange: null)
            .Should().Be(2.00m);
    }

    [Fact]
    public void ResolveDistance_ShouldMultiplyTheAverageTrueRange_WhenAValueIsAvailable()
    {
        StopProximity.AverageTrueRange(2m)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_295m), averageTrueRange: 3.5m)
            .Should().Be(7.0m);
    }

    [Fact]
    public void ResolveDistance_ShouldBeNull_WhenNoAverageTrueRangeIsAvailable()
    {
        // Insufficient history, or a projection that has not caught up. Null means "cannot measure", which the
        // caller must turn into NOT PROMOTING. Substituting a default distance here is exactly the silent
        // mis-measurement the old NotSupportedException existed to prevent.
        StopProximity.AverageTrueRange(2m)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_295m), averageTrueRange: null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0)]   // a flat-to-the-tick series is not a measurement, and a zero band is not this plan's intent
    [InlineData(-1)]  // impossible from Wilder's smoothing; if it ever arrives it is corruption, not a distance
    public void ResolveDistance_ShouldBeNull_WhenTheAverageTrueRangeIsNotPositive(decimal atr)
    {
        StopProximity.AverageTrueRange(2m)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_295m), atr)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveDistance_ShouldIgnoreTheAverageTrueRange_ForAMetricThatDoesNotUseIt()
    {
        // A stray ATR reading must never perturb a tick band -- the metric alone decides which inputs matter.
        StopProximity.Ticks(4)
            .ResolveDistance(Mes, new Price(5_300m), new Price(5_295m), averageTrueRange: 99m)
            .Should().Be(1.00m);
    }
}
