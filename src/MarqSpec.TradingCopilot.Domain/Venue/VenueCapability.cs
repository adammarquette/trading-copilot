namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// What a venue can do. Venues differ — a data-only provider has no execution at all, historical bars may sit
/// behind a paid tier, and order types vary — so capability differences are <b>explicit</b> and callers degrade
/// gracefully instead of discovering a gap at execution time (R-17).
/// </summary>
[Flags]
public enum VenueCapability
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Historical OHLCV bars over REST.</summary>
    HistoricalBars = 1 << 0,

    /// <summary>Streaming best bid/ask quotes.</summary>
    Quotes = 1 << 1,

    /// <summary>Streaming depth of market (DOM).</summary>
    MarketDepth = 1 << 2,

    /// <summary>Streaming order, position, and fill events for an account.</summary>
    AccountStreaming = 1 << 3,

    /// <summary>Attaching an OCO stop-loss / take-profit bracket to an order.</summary>
    BracketOrders = 1 << 4,

    /// <summary>Trailing-stop order type.</summary>
    TrailingStops = 1 << 5,

    /// <summary>Modifying a working order in place (rather than cancel/replace).</summary>
    ModifyOrder = 1 << 6,

    /// <summary>Closing a position outright — the capability auto-flatten depends on (R-13).</summary>
    ClosePosition = 1 << 7,

    /// <summary>
    /// Non-market <b>news</b> / soft-signal items over REST (R-2). A data-only news provider (Finnhub, Tiingo)
    /// grants this and little else.
    /// </summary>
    News = 1 << 8,

    /// <summary>
    /// Streaming <b>last-trade prints</b> for cross-asset <b>context</b> (R-1, gh#496) — SPY ↔ ES, QQQ ↔ NQ.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> <see cref="Quotes"/>. A context feed publishes the price a trade printed at, with no
    /// book behind it: Finnhub's free tier carries neither side of the top of book, so a source granting this could
    /// only satisfy <see cref="Quotes"/> by inventing a zero spread — a fabricated number in the one stream the
    /// execution watchers act on. Keeping the capability distinct is what lets an executable-price consumer refuse
    /// a context source at the seam instead of discovering the difference at execution time.
    /// </remarks>
    ContextTrades = 1 << 9,

    /// <summary>
    /// Sizing a partial close — taking a position <b>part-way toward flat</b> by fewer contracts than it holds
    /// (gh#928). Deliberately distinct from <see cref="ClosePosition"/>: a venue can close a position outright
    /// without being able to size a partial, so a caller that needs the sized variant must check for <i>this</i>
    /// and degrade loudly (R-17) rather than fall back to flattening the whole thing.
    /// </summary>
    ReducePosition = 1 << 10,
}
