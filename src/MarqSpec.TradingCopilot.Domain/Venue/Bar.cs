namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>One OHLCV bar. Prices are <see cref="Price"/> (exact decimal), never floating point.</summary>
/// <param name="OpenTime">The bar's opening timestamp.</param>
/// <param name="Open">The opening price.</param>
/// <param name="High">The highest traded price in the bar.</param>
/// <param name="Low">The lowest traded price in the bar.</param>
/// <param name="Close">The closing price.</param>
/// <param name="Volume">The traded volume in the bar.</param>
public sealed record Bar(
    DateTimeOffset OpenTime,
    Price Open,
    Price High,
    Price Low,
    Price Close,
    long Volume);
