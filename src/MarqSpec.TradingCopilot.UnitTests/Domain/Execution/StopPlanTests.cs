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
        StopPlan plan = BuyPlan(); // actual 5295, 4 ticks x 0.25 = 1.00 -> promote at <= 5296.00

        plan.ShouldPromote(new Price(5_299m)).Should().BeFalse();
        plan.ShouldPromote(new Price(5_296.25m)).Should().BeFalse();
    }

    [Fact]
    public void ShouldPromote_ShouldBeTrue_OnceWithinTheProximityBand_OnABuy()
    {
        StopPlan plan = BuyPlan();

        plan.ShouldPromote(new Price(5_296m)).Should().BeTrue();    // exactly at the band edge
        plan.ShouldPromote(new Price(5_295.5m)).Should().BeTrue();
        plan.ShouldPromote(new Price(5_294m)).Should().BeTrue();    // already through the stop
    }

    [Fact]
    public void ShouldPromote_ShouldMirrorTheBand_OnASell()
    {
        StopPlan plan = SellPlan(); // actual 5305, band 1.00 -> promote at >= 5304.00

        plan.ShouldPromote(new Price(5_301m)).Should().BeFalse();
        plan.ShouldPromote(new Price(5_304m)).Should().BeTrue();
        plan.ShouldPromote(new Price(5_306m)).Should().BeTrue();
    }

    [Fact]
    public void ShouldPromote_ShouldMeasureAFractionOfTheEntryToStopDistance_NotOfRawPrice()
    {
        // ADR-0007 is explicit: proximity is ticks / ATR / a fraction of the entry->stop distance -- NEVER a
        // percentage of raw price, which would scale with the instrument's absolute level rather than the risk.
        StopPlan plan = BuyPlan(entry: 5_300m, actualStop: 5_290m, safetyStop: 5_280m,
            proximity: StopProximity.DistanceFraction(0.2m)); // 20% of a 10-point distance = 2.00

        plan.ShouldPromote(new Price(5_292.25m)).Should().BeFalse();
        plan.ShouldPromote(new Price(5_292m)).Should().BeTrue();
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
        plan.ShouldPromote(new Price(5_294m)).Should().BeFalse();
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

    [Fact]
    public void Create_ShouldRefuseAnAverageTrueRangeBand_WhileIndicatorDataIsUnavailable()
    {
        // The ADR names ATR as a proximity metric; the indicator pipeline (R-3) does not exist yet. Refuse it
        // loudly rather than silently treating it as ticks -- the whitelist discipline.
        Func<StopPlan> act = () => BuyPlan(proximity: StopProximity.AverageTrueRange(2m));

        act.Should().Throw<NotSupportedException>();
    }
}
