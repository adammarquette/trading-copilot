namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>A best bid/ask quote at a point in time.</summary>
/// <param name="Timestamp">When the venue published the quote.</param>
/// <param name="Bid">The best bid price.</param>
/// <param name="Ask">The best ask price.</param>
/// <param name="BidSize">Size resting at the bid.</param>
/// <param name="AskSize">Size resting at the ask.</param>
public sealed record Quote(
    DateTimeOffset Timestamp,
    Price Bid,
    Price Ask,
    long BidSize,
    long AskSize);
