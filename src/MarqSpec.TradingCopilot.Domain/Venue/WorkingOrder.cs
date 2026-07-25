namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue-neutral view of an order <b>resting live at the venue</b> (R-17, gh#183): the venue handle, the
/// contract, and the resting price — what a caller needs to identify a leg and audit it, not the whole order
/// model. Used by OCO-cancel-on-exit to find the protective legs still standing on a contract that has gone flat.
/// </summary>
/// <param name="VenueOrderKey">The venue's own order handle — the key <see cref="IOrderExecutor.CancelOrderAsync"/> cancels by.</param>
/// <param name="Contract">The contract the order rests on.</param>
/// <param name="StopPrice">The stop trigger, when the order carries one (a protective stop leg does).</param>
/// <param name="LimitPrice">The limit price, when the order carries one (a take-profit leg does).</param>
public sealed record WorkingOrder(
    string VenueOrderKey,
    VenueContractId Contract,
    Price? StopPrice,
    Price? LimitPrice);
