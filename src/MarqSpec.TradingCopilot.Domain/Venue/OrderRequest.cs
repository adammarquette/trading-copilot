namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A ticket to send to a venue. Constructing one is <b>not</b> permission to send it — the risk model, the
/// execution gate, and the sanity caps (R-5 / R-12 / R-16) sit between this and
/// <see cref="IOrderExecutor.PlaceOrderAsync"/>.
/// </summary>
/// <param name="Account">The account to trade.</param>
/// <param name="Contract">The contract to trade.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="Type">The order type.</param>
/// <param name="Quantity">Contract quantity; always positive — direction comes from <paramref name="Side"/>.</param>
/// <param name="LimitPrice">The limit price, where the order type requires one.</param>
/// <param name="StopPrice">The stop price, where the order type requires one.</param>
public sealed record OrderRequest(
    VenueAccountId Account,
    VenueContractId Contract,
    OrderSide Side,
    OrderType Type,
    int Quantity,
    Price? LimitPrice,
    Price? StopPrice)
{
    /// <summary>
    /// The <b>always-native safety stop</b> to attach to this entry (ADR-0007, gh#11): an exchange-held
    /// protective stop the venue places <i>on fill</i>, so a live position is never without a real stop —
    /// covering gaps, fast moves, and outages. Requires <see cref="VenueCapability.BracketOrders"/>; the
    /// execution service refuses to transmit an entry a venue cannot protect. <c>null</c> only where no entry
    /// is being opened (a flatten or a stand-alone exit).
    /// </summary>
    public Price? ProtectiveStop { get; init; }
}
