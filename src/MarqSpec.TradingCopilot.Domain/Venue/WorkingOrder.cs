namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue-neutral view of an order <b>resting live at the venue</b> (R-17, gh#183): the venue handle, the
/// contract, the resting price, and how much it covers — what a caller needs to identify a leg and audit it, not
/// the whole order model. Used by OCO-cancel-on-exit to find the protective legs still standing on a contract
/// that has gone flat, and by the venue-truth resting-orders read (gh#381).
/// </summary>
/// <remarks>
/// <b>The view stays deliberately narrow.</b> It is not the order model — no side, type, status, or timestamps —
/// because a wider view invites callers to make execution decisions from a read that is a snapshot by nature.
/// <see cref="Size"/> was added (gh#381) because <i>how much of the position a protective leg actually covers</i>
/// is not an ornament: a bracket sized to less than the position leaves the remainder unprotected, and neither
/// the operator nor a staging gate could see that through the app before.
/// </remarks>
/// <param name="VenueOrderKey">The venue's own order handle — the key <see cref="IOrderExecutor.CancelOrderAsync"/> cancels by.</param>
/// <param name="Contract">The contract the order rests on.</param>
/// <param name="StopPrice">The stop trigger, when the order carries one (a protective stop leg does).</param>
/// <param name="LimitPrice">The limit price, when the order carries one (a take-profit leg does).</param>
/// <param name="Size">
/// The quantity the order covers. <b>Not a venue limitation that this was ever absent</b> — the ProjectX gateway
/// order has carried it throughout; the projection simply dropped it (gh#381).
/// </param>
public sealed record WorkingOrder(
    string VenueOrderKey,
    VenueContractId Contract,
    Price? StopPrice,
    Price? LimitPrice,
    int Size)
{
    /// <summary>
    /// The <b>client-supplied correlation handle</b> the placing request stamped (gh#577), echoed back by the venue
    /// — the one field admitted to this otherwise-narrow view precisely because it is an <i>identity</i> handle, not
    /// an execution-decision field: it lets a replay recognise <i>its own</i> already-placed order (a conditional
    /// left live by a transmit→journal fault matches on its firing conditional's id) rather than transmitting a
    /// duplicate. <c>null</c> for a leg the venue spawned itself (a protective bracket carries no client tag).
    /// </summary>
    public string? CustomTag { get; init; }
}
