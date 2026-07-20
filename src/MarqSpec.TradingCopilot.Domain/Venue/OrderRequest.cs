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
    Price? StopPrice);
