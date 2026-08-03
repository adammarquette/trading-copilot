namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// The pure lifecycle decision for a suggestion (R-4, gh#545, ADR-0013): given a suggestion's current state, its
/// validity window, and the current time, the state it should hold now. Deterministic and side-effect-free — the
/// steady-state expire <b>sweep</b> and the <b>startup recovery</b> pass both drive from this one function, so
/// recovery and normal operation cannot diverge (the first pass after a restart <i>is</i> the recovery catch-up,
/// not a separate resurrection path).
/// </summary>
/// <remarks>
/// It only ever moves a suggestion <b>forward</b>: the sole transition is a <b>live</b> suggestion
/// (<see cref="SuggestionState.Active"/> or <see cref="SuggestionState.Stale"/>) past its validity window →
/// <see cref="SuggestionState.ExpiredVoid"/>. A resolved suggestion is never chased back to life and a returned price
/// can never un-expire a row (ADR-0013's *"a scratched setup is not chased"* made structural); a re-formed setup
/// issues a new superseding suggestion (gh#550). Expiry is <b>time</b>, not drift — deriving <see cref="SuggestionState.Stale"/>
/// needs market input this function is deliberately not given (that is the drift consumer's, gh#546).
/// </remarks>
public static class SuggestionLifecycle
{
    /// <summary>
    /// Decides the state a suggestion should hold at <paramref name="now"/>. A live suggestion whose window has
    /// passed (<paramref name="now"/> at or after <paramref name="expiresAt"/>) becomes
    /// <see cref="SuggestionState.ExpiredVoid"/>; every other case returns the state <b>unchanged</b> — within the
    /// window it is left as it is, and a terminal <see cref="SuggestionState.ExpiredVoid"/> or the refusable
    /// <see cref="SuggestionState.Unknown"/> never transitions. Idempotent: applying it to its own result is a no-op.
    /// </summary>
    /// <param name="state">The suggestion's current lifecycle state.</param>
    /// <param name="expiresAt">When the suggestion stops being actionable (gh#544).</param>
    /// <param name="now">The current time, supplied by the caller — the decision never reads a clock.</param>
    /// <returns>The state the suggestion should hold now.</returns>
    public static SuggestionState Decide(SuggestionState state, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        // Only a LIVE suggestion expires, and only its window passing triggers it. The boundary is inclusive: a
        // suggestion "stops being actionable" AT expiry, so now == expiresAt already expires it (a strict '>' would
        // leave a just-expired suggestion actionable for one more tick). Everything else is left exactly as it is.
        bool isLive = state is SuggestionState.Active or SuggestionState.Stale;
        return isLive && now >= expiresAt ? SuggestionState.ExpiredVoid : state;
    }
}
