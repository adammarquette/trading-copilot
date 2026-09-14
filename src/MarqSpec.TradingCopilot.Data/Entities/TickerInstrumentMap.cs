namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One ticker↔instrument mapping (gh#359, R-2) — a provider ticker (<c>SPY</c>) resolves to a traded instrument
/// (<c>ES</c>). <b>Deployment-global config</b> (a market fact, not an operator preference), so deliberately not
/// <c>IUserOwned</c>, like the news it maps; the per-user salience that reweights news is a separate entity (gh#27).
/// The key is the pair, so a ticker can map to several instruments and several tickers to one.
/// </summary>
public sealed class TickerInstrumentMap
{
    /// <summary>The provider ticker as it appears on a news item (normalized upper-case).</summary>
    public required string Ticker { get; set; }

    /// <summary>The traded instrument symbol it maps to (normalized upper-case).</summary>
    public required string Instrument { get; set; }
}
