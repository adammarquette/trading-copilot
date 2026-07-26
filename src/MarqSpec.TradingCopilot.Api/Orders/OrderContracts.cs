using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Api.Orders;

/// <summary>
/// The request to send one order through the gate (gh#11). The instrument's tick size and point value ride the
/// request because no Instrument entity exists yet (recorded on the dictionary §1 row) — the caller supplies
/// the spec the risk math needs.
/// </summary>
/// <param name="Symbol">The venue-neutral instrument symbol (e.g. <c>MES</c>).</param>
/// <param name="TickSize">The instrument's tick size.</param>
/// <param name="PointValue">The instrument's point value (currency per point per contract).</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="Quantity">The contracts asked for — the gate decides what is actually authorized.</param>
/// <param name="Entry">The intended entry price.</param>
/// <param name="Stop">The working stop price.</param>
/// <param name="SafetyStop">The safety-stop price — the deterministic worst case (R-5).</param>
/// <param name="ReferencePrice">The current reference price the fat-finger band measures from.</param>
/// <param name="Type">How the order rests. Trailing stops are refused (no trail distance on the ticket).</param>
/// <param name="Target">
/// The optional take-profit price (ADR-0007, gh#173) — the profit side of a native OCO bracket. It must sit on
/// the <b>winning</b> side of <paramref name="Entry"/> (above for a long, below for a short); a wrong-side
/// target is refused before the gate. <see langword="null"/> means no profit leg (the two-leg bracket).
/// </param>
public sealed record SendOrderRequest(
    string Symbol,
    decimal TickSize,
    decimal PointValue,
    OrderSide Side,
    int Quantity,
    decimal Entry,
    decimal Stop,
    decimal SafetyStop,
    decimal ReferencePrice,
    OrderType Type = OrderType.Market,
    decimal? Target = null);

/// <summary>
/// The request to reprice a <b>working</b> order in place — a PATCH that moves the resting <b>entry</b> at the venue
/// (gh#259), re-stages the hidden <b>working stop</b> (gh#267), or does <b>both together</b> in one commit (gh#278).
/// At least one of <see cref="EntryPrice"/> / <see cref="WorkingStopPrice"/> must be present. <b>Size</b> and the
/// <b>safety stop</b> stay invariant on every path, so the R-5 catastrophic floor and the attached safety bracket's
/// coverage are untouched; the full chain <c>safety → working → entry</c> is re-validated before the venue is touched.
/// </summary>
/// <param name="EntryPrice">
/// The new entry price, or <see langword="null"/> to leave it. When present, the entry reprices at the venue and
/// re-gates (gh#259); <see cref="ReferencePrice"/> is then required.
/// </param>
/// <param name="WorkingStopPrice">
/// The new <b>hidden</b> working-stop price, or <see langword="null"/> to leave it (gh#267). A hidden stop is a
/// promotion target, not a venue order, so re-staging it is a <b>local</b> write — no venue call. It re-gates
/// <b>only</b> when it <i>widens</i> under <c>SizingBasis.ActualStop</c> (the one enforced layer measured at the
/// working stop). Moving it onto the safety stop (removing it) is out of scope.
/// </param>
/// <param name="ReferencePrice">
/// The <b>caller's</b> current market reference the fat-finger band re-measures a new <b>entry</b> against (R-16)
/// — <b>required when <see cref="EntryPrice"/> is present</b>. Caller-supplied, as on every order path (the server
/// fetches no venue quote), so it is the client's freshness, not the venue's. A working-stop-only re-stage does
/// not move the entry, so it is unused there.
/// </param>
public sealed record ModifyWorkingOrderRequest(
    decimal? EntryPrice,
    decimal? WorkingStopPrice = null,
    decimal? ReferencePrice = null);

/// <summary>The outcome of a send attempt — always explains itself (R-5).</summary>
/// <param name="Outcome">Placed, or which guard refused.</param>
/// <param name="OrderId">The journaled order's id, when placed.</param>
/// <param name="VenueOrderKey">The venue's own order handle, when placed.</param>
/// <param name="ApprovedQuantity">The contracts the gate authorized (0 when blocked or never sized).</param>
/// <param name="BindingLayer">The risk layer that bound the decision, when one did.</param>
/// <param name="Reason">The human-readable why — always populated.</param>
public sealed record SendOrderResponse(
    string Outcome,
    Guid? OrderId,
    string? VenueOrderKey,
    int ApprovedQuantity,
    RiskLayer? BindingLayer,
    string Reason);

/// <summary>A staged (armed) order with its latest gate decision (gh#11 increment 2, ADR-0007).</summary>
/// <param name="OrderId">The staged order's id.</param>
/// <param name="Status">The order status (<c>Staged</c> until taken or cancelled).</param>
/// <param name="Outcome">The latest gate outcome — allowing, resizing, or blocking; staging keeps all three.</param>
/// <param name="ApprovedQuantity">The contracts the gate would authorize right now.</param>
/// <param name="BindingLayer">The risk layer that bound the decision, when one did.</param>
/// <param name="Reason">The gate's human-readable why — always populated (R-5).</param>
/// <param name="Target">The staged take-profit target, when one was set — so the operator sees and edits it (gh#173).</param>
public sealed record StagedOrderResponse(
    Guid OrderId,
    string Status,
    string Outcome,
    int ApprovedQuantity,
    RiskLayer? BindingLayer,
    string Reason,
    decimal? Target)
{
    /// <summary>Projects a staged row and its decision.</summary>
    /// <param name="order">The staged order.</param>
    /// <param name="decision">The evaluation that accompanied this staging or edit.</param>
    /// <returns>The response.</returns>
    public static StagedOrderResponse From(Data.Entities.Order order, GateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(decision);
        return new StagedOrderResponse(
            order.Id,
            order.Status.ToString(),
            decision.Outcome.ToString(),
            decision.ApprovedQuantity,
            decision.BindingLayer,
            decision.Reason,
            order.TakeProfitPrice);
    }
}

/// <summary>
/// The request to create a <b>synthetic conditional</b> entry order (gh#176, ADR-0007) — the "send when
/// conditions met" mode. The proposal rides an ordinary <see cref="SendOrderRequest"/>; the conditional part is
/// the trigger and its cancel-if / expiry.
/// </summary>
/// <param name="Order">The entry proposal to hold and fire when the trigger crosses.</param>
/// <param name="TriggerPrice">The price at which the order fires.</param>
/// <param name="TriggerDirection">Which way price must cross the trigger to fire (RisesTo / FallsTo).</param>
/// <param name="CancelDrift">The optional cancel band — the stale-side price past which the setup is abandoned.</param>
/// <param name="ExpiresAt">The optional validity deadline; past it, a still-pending order cancels.</param>
public sealed record CreateConditionalOrderRequest(
    SendOrderRequest Order,
    decimal TriggerPrice,
    ConditionalCrossDirection TriggerDirection,
    decimal? CancelDrift = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>A pending conditional order with its creation-time evaluation (gh#176).</summary>
/// <param name="ConditionalOrderId">The conditional order's id.</param>
/// <param name="Status">The lifecycle status (<c>Pending</c> until it fires, cancels, or expires).</param>
/// <param name="TriggerPrice">The price at which it fires.</param>
/// <param name="TriggerDirection">The cross direction.</param>
/// <param name="Outcome">The creation-time gate outcome — feedback only; the authoritative gate runs at fire time (R-12).</param>
/// <param name="ApprovedQuantity">The contracts the gate would authorize right now.</param>
/// <param name="BindingLayer">The risk layer that bound the evaluation, when one did.</param>
/// <param name="Reason">The gate's human-readable why — always populated (R-5).</param>
public sealed record ConditionalOrderResponse(
    Guid ConditionalOrderId,
    string Status,
    decimal TriggerPrice,
    string TriggerDirection,
    string Outcome,
    int ApprovedQuantity,
    RiskLayer? BindingLayer,
    string Reason)
{
    /// <summary>Projects a pending conditional record and its creation-time decision.</summary>
    /// <param name="record">The persisted conditional order.</param>
    /// <param name="decision">The evaluation that accompanied its creation.</param>
    /// <returns>The response.</returns>
    public static ConditionalOrderResponse From(Data.Entities.ConditionalOrderRecord record, GateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(decision);
        return new ConditionalOrderResponse(
            record.Id,
            record.Status.ToString(),
            record.TriggerPrice,
            record.TriggerDirection.ToString(),
            decision.Outcome.ToString(),
            decision.ApprovedQuantity,
            decision.BindingLayer,
            decision.Reason);
    }
}
