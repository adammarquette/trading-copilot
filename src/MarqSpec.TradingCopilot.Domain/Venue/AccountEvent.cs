namespace MarqSpec.TradingCopilot.Domain.Venue;

/// <summary>
/// A venue-neutral event on an account's realtime stream (R-17, gh#219): what happened to an order, a fill, or a
/// position after transmission. The adapter translates the venue's own payloads onto these at its boundary, so the
/// core is never blind to what happens to an order once it leaves — and no vendor type crosses into the domain.
/// </summary>
/// <param name="Account">The account the event is for.</param>
/// <param name="At">When the venue reported it (UTC).</param>
public abstract record AccountEvent(VenueAccountId Account, DateTimeOffset At);

/// <summary>
/// An order's lifecycle state changed at the venue. The consumer takes the terminal <b>non-fill</b> transitions
/// (cancelled / expired / rejected) from here; filled volume is driven by <see cref="FillEvent"/>s, which are the
/// authoritative record of executed size.
/// </summary>
/// <param name="Account">The account the order is on.</param>
/// <param name="At">When the venue reported the change (UTC).</param>
/// <param name="VenueOrderKey">The venue's own order handle — resolves back to the journaled order.</param>
/// <param name="State">The neutral lifecycle state.</param>
/// <param name="FilledQuantity">The cumulative filled quantity the venue reports on the order.</param>
/// <param name="AverageFillPrice">The average fill price, when the venue reports one.</param>
public sealed record OrderStateEvent(
    VenueAccountId Account,
    DateTimeOffset At,
    string VenueOrderKey,
    VenueOrderState State,
    int FilledQuantity,
    Price? AverageFillPrice) : AccountEvent(Account, At);

/// <summary>
/// A single execution (fill) against an order. <see cref="VenueFillKey"/> is the venue's own trade handle — the
/// natural key the persistence layer uses to record each fill exactly once (idempotent by construction).
/// </summary>
/// <param name="Account">The account the fill is on.</param>
/// <param name="At">When the execution occurred (UTC).</param>
/// <param name="VenueOrderKey">The venue order handle the fill is against — resolves to the journaled order.</param>
/// <param name="VenueFillKey">The venue's own trade/fill handle — the idempotency key.</param>
/// <param name="Side">Which side executed.</param>
/// <param name="Quantity">The executed quantity (positive).</param>
/// <param name="ExecutionPrice">The execution price.</param>
/// <param name="Fees">The fees the venue charged for this execution.</param>
/// <param name="Voided">Whether the venue busted (voided) this trade — a voided fill is not counted.</param>
public sealed record FillEvent(
    VenueAccountId Account,
    DateTimeOffset At,
    string VenueOrderKey,
    string VenueFillKey,
    OrderSide Side,
    int Quantity,
    Price ExecutionPrice,
    decimal Fees,
    bool Voided) : AccountEvent(Account, At);

/// <summary>
/// A position's net exposure changed at the venue. Carried by the seam for completeness (and future consumers,
/// e.g. OCO-cancel-on-exit, gh#183); position <i>truth</i> for reconciliation comes from the venue query path
/// (gh#193), so this consumer does not persist it.
/// </summary>
/// <param name="Account">The account the position is on.</param>
/// <param name="At">When the venue reported the change (UTC).</param>
/// <param name="Contract">The contract the position is on.</param>
/// <param name="NetQuantity">The signed net exposure (positive long, negative short, zero flat).</param>
/// <param name="AveragePrice">The position's average price.</param>
public sealed record PositionEvent(
    VenueAccountId Account,
    DateTimeOffset At,
    VenueContractId Contract,
    int NetQuantity,
    Price AveragePrice) : AccountEvent(Account, At);
