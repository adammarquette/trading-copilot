namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>A best bid/ask quote at a point in time.</summary>
/// <param name="Timestamp">When the venue published the quote.</param>
/// <param name="Bid">The best bid price.</param>
/// <param name="Ask">The best ask price.</param>
/// <param name="BidSize">
/// Size resting at the bid, where the venue publishes it on the quote channel — <see langword="null"/> when it
/// does not. ProjectX's quote stream carries prices only; resting size comes from the depth stream
/// (<see cref="VenueCapability.MarketDepth"/>), so this is absent rather than zero.
/// </param>
/// <param name="AskSize">Size resting at the ask, or <see langword="null"/>. See <paramref name="BidSize"/>.</param>
public sealed record Quote(
    DateTimeOffset Timestamp,
    Price Bid,
    Price Ask,
    long? BidSize,
    long? AskSize);
