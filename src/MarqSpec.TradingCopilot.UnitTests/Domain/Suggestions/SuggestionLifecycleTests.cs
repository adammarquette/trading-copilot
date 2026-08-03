using MarqSpec.TradingCopilot.Domain.Suggestions;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Suggestions;

/// <summary>
/// The pure suggestion-lifecycle decisions (gh#545, gh#546, ADR-0013): the time-driven <see cref="SuggestionLifecycle.Decide"/>
/// (a live suggestion past its validity window expires; everything else unchanged — forward-only and idempotent) and
/// the market-driven <see cref="SuggestionLifecycle.HasDrifted"/> (whether the current quote has moved past the entry
/// tolerance). The sweep and the drift consumer drive from these, so these cases pin what each does.
/// </summary>
public class SuggestionLifecycleTests
{
    private static DateTimeOffset Expiry { get; } = DateTimeOffset.UnixEpoch.AddYears(56);

    [Theory]
    [InlineData(SuggestionState.Active)]
    [InlineData(SuggestionState.Stale)]
    public void Decide_ShouldExpire_WhenALiveSuggestionIsPastItsWindow(SuggestionState state)
    {
        SuggestionLifecycle.Decide(state, Expiry, Expiry.AddSeconds(1)).Should().Be(SuggestionState.ExpiredVoid);
    }

    [Theory]
    [InlineData(SuggestionState.Active)]
    [InlineData(SuggestionState.Stale)]
    public void Decide_ShouldExpire_AtTheExactBoundary(SuggestionState state)
    {
        // now == expiresAt: the window is closed. A suggestion "stops being actionable" AT expiry (inclusive), so the
        // boundary expires — a strict '>' would leave a just-expired suggestion actionable for one more tick.
        SuggestionLifecycle.Decide(state, Expiry, Expiry).Should().Be(SuggestionState.ExpiredVoid);
    }

    [Theory]
    [InlineData(SuggestionState.Active)]
    [InlineData(SuggestionState.Stale)]
    public void Decide_ShouldLeaveUnchanged_WhenWithinTheWindow(SuggestionState state)
    {
        // A Stale suggestion within its window stays Stale — a drift that resolves does NOT un-stale it (ADR-0013);
        // an Active one stays Active. Decide only ever moves forward.
        SuggestionLifecycle.Decide(state, Expiry, Expiry.AddSeconds(-1)).Should().Be(state);
    }

    [Fact]
    public void Decide_ShouldBeIdempotent_OnAnAlreadyExpiredSuggestion()
    {
        // ExpiredVoid is terminal — it never transitions again, even long past the window. Applying Decide to its own
        // result is a no-op, so the sweep and recovery pass are safe to re-run.
        SuggestionLifecycle.Decide(SuggestionState.ExpiredVoid, Expiry, Expiry.AddYears(1))
            .Should().Be(SuggestionState.ExpiredVoid);
    }

    [Fact]
    public void Decide_ShouldNeverTransition_TheRefusableUnknown()
    {
        // Unknown is never persisted; if one is ever seen it must not be silently "expired" into a real state.
        SuggestionLifecycle.Decide(SuggestionState.Unknown, Expiry, Expiry.AddYears(1))
            .Should().Be(SuggestionState.Unknown);
    }

    // ---- HasDrifted: the market-driven decision (gh#546). Entry 5300, tolerance 2.0 (8 ticks x 0.25 on ES). ----

    [Theory]
    [InlineData(OrderSide.Buy, 5_301.00)]   // ask 1.0 above entry — within the 2.0 band
    [InlineData(OrderSide.Buy, 5_299.00)]   // ask 1.0 below entry — within, and symmetric
    [InlineData(OrderSide.Sell, 5_301.00)]  // bid 1.0 above entry — within
    public void HasDrifted_ShouldBeFalse_WhenWithinTolerance(OrderSide side, decimal price)
    {
        // The side-appropriate price sits inside the band, so the setup is still takeable at its level.
        SuggestionLifecycle.HasDrifted(side, entry: 5_300m, bid: price, ask: price, tolerance: 2m).Should().BeFalse();
    }

    [Theory]
    [InlineData(OrderSide.Buy, 5_303.00)]   // ask ran 3.0 above entry — missed the long
    [InlineData(OrderSide.Sell, 5_297.00)]  // bid ran 3.0 below entry — missed the short
    public void HasDrifted_ShouldBeTrue_WhenBeyondTolerance(OrderSide side, decimal price)
    {
        SuggestionLifecycle.HasDrifted(side, entry: 5_300m, bid: price, ask: price, tolerance: 2m).Should().BeTrue();
    }

    [Fact]
    public void HasDrifted_ShouldBeFalse_AtTheExactBoundary()
    {
        // |price - entry| == tolerance is AT the band, not past it — the strict '>' matches the take-time re-check
        // (gh#548), so a suggestion exactly at tolerance is not yet stale.
        SuggestionLifecycle.HasDrifted(OrderSide.Buy, entry: 5_300m, bid: 5_302m, ask: 5_302m, tolerance: 2m).Should().BeFalse();
    }

    [Fact]
    public void HasDrifted_ShouldBeTrue_JustBeyondTheBoundary()
    {
        SuggestionLifecycle.HasDrifted(OrderSide.Buy, entry: 5_300m, bid: 5_302.25m, ask: 5_302.25m, tolerance: 2m).Should().BeTrue();
    }

    [Fact]
    public void HasDrifted_ShouldBeSymmetric_ForAFavourableMove()
    {
        // A LONG whose price fell 3 points below entry has also drifted: R-4/R-12 measure distance from the entry in
        // EITHER direction (deliberately unlike the conditional order's directional cancel-band) — the level is gone,
        // and a re-formed setup is a new suggestion, not a chase.
        SuggestionLifecycle.HasDrifted(OrderSide.Buy, entry: 5_300m, bid: 5_297m, ask: 5_297m, tolerance: 2m).Should().BeTrue();
    }

    [Fact]
    public void HasDrifted_ShouldMeasureTheAsk_ForALong()
    {
        // A long enters by BUYING at the ask. A drifted bid with the ask still at entry is NOT drift for a long — it
        // is the ask that decides.
        SuggestionLifecycle.HasDrifted(OrderSide.Buy, entry: 5_300m, bid: 5_290m, ask: 5_300m, tolerance: 2m).Should().BeFalse();
        SuggestionLifecycle.HasDrifted(OrderSide.Buy, entry: 5_300m, bid: 5_300m, ask: 5_310m, tolerance: 2m).Should().BeTrue();
    }

    [Fact]
    public void HasDrifted_ShouldMeasureTheBid_ForAShort()
    {
        // A short enters by SELLING at the bid. A drifted ask with the bid still at entry is NOT drift for a short.
        SuggestionLifecycle.HasDrifted(OrderSide.Sell, entry: 5_300m, bid: 5_300m, ask: 5_310m, tolerance: 2m).Should().BeFalse();
        SuggestionLifecycle.HasDrifted(OrderSide.Sell, entry: 5_300m, bid: 5_290m, ask: 5_300m, tolerance: 2m).Should().BeTrue();
    }
}
