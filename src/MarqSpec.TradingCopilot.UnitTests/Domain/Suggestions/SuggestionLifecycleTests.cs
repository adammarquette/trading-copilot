using MarqSpec.TradingCopilot.Domain.Suggestions;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Suggestions;

/// <summary>
/// The pure suggestion-lifecycle decision (gh#545, ADR-0013): a live suggestion past its validity window expires;
/// everything else is left unchanged — forward-only and idempotent. The steady-state sweep and the startup recovery
/// pass both drive from this one function, so these cases pin what <b>both</b> do.
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
}
