namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>
/// What a reported position's mark rests on (R-13, R-19, ADR-0013, gh#193) — so a settlement re-mark is never
/// mistaken for live movement, and an unobtainable truth is never shown as a live number.
/// </summary>
public enum PositionMarkBasis
{
    /// <summary>
    /// Truth could not be obtained from the venue — a <b>declared-unknown</b> state. Fail-safe: never present a
    /// stale local number as live; the operator is told the state is unknown, not shown a guess (R-19, §9).
    /// </summary>
    Unknown = 0,

    /// <summary>The venue is in its live trading session — the mark is a live market price.</summary>
    Live = 1,

    /// <summary>
    /// The venue is in its daily <b>settlement / maintenance window</b>: any position carried through is
    /// re-marked at the settlement price, which is <b>not</b> live movement and must not be read as the market
    /// moving against the operator (R-13).
    /// </summary>
    Settlement = 2,
}
