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
}
