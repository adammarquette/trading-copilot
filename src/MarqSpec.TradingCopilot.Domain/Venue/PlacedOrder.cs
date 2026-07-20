namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>A venue's acknowledgement that it accepted an order, and the handle to act on it afterwards.</summary>
/// <param name="Account">The account the order was placed on.</param>
/// <param name="VenueOrderId">The venue's own order handle, used to modify or cancel.</param>
/// <param name="AcceptedAt">When the venue accepted the order.</param>
public sealed record PlacedOrder(
    VenueAccountId Account,
    string VenueOrderId,
    DateTimeOffset AcceptedAt);
