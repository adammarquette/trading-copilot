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
