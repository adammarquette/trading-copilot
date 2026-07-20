namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// Whether an account trades simulated or real money. Venues express this differently — ProjectX encodes it in the
/// account <b>name</b> (<c>PRAC-…</c> vs <c>50KTC-…</c>) with no mode field, while Tradovate splits it by
/// <b>host</b> (demo vs live) — so each adapter derives it and the core sees only this venue-neutral value (R-17).
/// </summary>
public enum TradingMode
{
    /// <summary>A practice / simulated account — the real execution path, no real money.</summary>
    Practice,

    /// <summary>A real-money account. Permitted in production only (R-14, <see cref="TradingModePolicy"/>).</summary>
    Live,
}
