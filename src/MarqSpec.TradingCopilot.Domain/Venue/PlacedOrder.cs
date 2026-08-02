namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>A venue's acknowledgement that it accepted an order, and the handle to act on it afterwards.</summary>
/// <param name="Account">The account the order was placed on.</param>
/// <param name="VenueOrderId">The venue's own order handle, used to modify or cancel.</param>
/// <param name="AcceptedAt">When the venue accepted the order.</param>
public sealed record PlacedOrder(
    VenueAccountId Account,
    string VenueOrderId,
    DateTimeOffset AcceptedAt)
{
    /// <summary>
    /// The <b>client-supplied correlation handle</b> (gh#577) this order carries, if the request set one — the same
    /// key the venue echoes on the resting-orders read, so a caller can tie the acknowledgement back to the source
    /// that placed it. <c>null</c> when the request carried no tag.
    /// </summary>
    public string? CustomTag { get; init; }
}
