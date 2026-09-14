using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.Suggestions;

/// <summary>
/// The pure lifecycle decisions for a suggestion (R-4, gh#545, gh#546, ADR-0013): the <b>time</b>-driven
/// <see cref="Decide"/> — the state it should hold now given its validity window and the clock — and the
/// <b>market</b>-driven <see cref="HasDrifted"/> — whether the current quote has moved past the entry tolerance.
/// Both are deterministic and side-effect-free; the steady-state expire <b>sweep</b>, the <b>startup recovery</b> pass
/// and the <b>drift consumer</b> drive from these one authorities, so recovery and normal operation cannot diverge.
/// </summary>
/// <remarks>
/// The lifecycle only ever moves a suggestion <b>forward</b> (<see cref="SuggestionState.Active"/> → drift →
/// <see cref="SuggestionState.Stale"/> → time → <see cref="SuggestionState.ExpiredVoid"/>): <see cref="Decide"/>'s sole
/// transition is a <b>live</b> suggestion (Active or Stale) past its validity window → ExpiredVoid, and
/// <see cref="HasDrifted"/> is a stateless predicate the drift consumer applies only to Active rows. A resolved
/// suggestion is never chased back to life and a returned price can never un-stale or un-expire a row (ADR-0013's
/// *"a scratched setup is not chased"* made structural); a re-formed setup issues a new superseding suggestion
/// (gh#550). Neither decision reads a clock or a quote — the caller supplies both, so both stay testable.
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

    /// <summary>
    /// Whether the current quote has drifted past the entry tolerance for a suggestion of the given side (gh#546,
    /// R-4 / R-12). The distance is measured from the <b>achievable</b> entry price — a long enters by <b>buying</b>
    /// at the <paramref name="ask"/>, a short by <b>selling</b> at the <paramref name="bid"/> — and it is
    /// <b>symmetric</b>: a move past the tolerance in <i>either</i> direction is drift (R-4/R-12 measure "within
    /// tolerance of the entry", deliberately unlike the conditional order's directional cancel-band). The boundary is
    /// exclusive (<c>&gt;</c>), matching the take-time re-check (gh#548): exactly at tolerance is not yet drifted.
    /// </summary>
    /// <param name="side">The suggestion's side — selects the achievable entry price (ask for a long, bid for a short).</param>
    /// <param name="entry">The suggested entry price.</param>
    /// <param name="bid">The current best bid.</param>
    /// <param name="ask">The current best ask.</param>
    /// <param name="tolerance">The drift band as a price distance (ticks × tick size, resolved by the caller).</param>
    /// <returns><see langword="true"/> when the market has moved more than <paramref name="tolerance"/> from the entry.</returns>
    public static bool HasDrifted(OrderSide side, decimal entry, decimal bid, decimal ask, decimal tolerance)
    {
        decimal price = side switch
        {
            OrderSide.Buy => ask,
            OrderSide.Sell => bid,
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unrecognized order side."),
        };
        return Math.Abs(price - entry) > tolerance;
    }
}
