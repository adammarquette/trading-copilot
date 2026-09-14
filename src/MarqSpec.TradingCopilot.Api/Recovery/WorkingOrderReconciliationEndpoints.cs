using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The resting-orders read (gh#381): reports an account's working orders — <b>including the attached protective
/// bracket and its size</b> — from venue truth, tagged with the same basis as the positions read (R-1, R-13,
/// R-17, ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Recovery</c> alongside <see cref="PositionReconciliationEndpoints"/> — the app's <b>venue-truth
/// read family</b> — deliberately <i>not</i> in the <c>OrderEndpoints</c> command group, which is the
/// journal-backed write surface. A <c>GET</c> and a <c>POST</c> on the same <c>/accounts/{id}/orders</c> template
/// across the two registrations do not collide.
/// </para>
/// <para>
/// <b>Read-only.</b> Nothing execution-shaped: the risk / execution gate is untouched.
/// </para>
/// </remarks>
public static class WorkingOrderReconciliationEndpoints
{
    /// <summary>Maps the resting-orders read. Requires authentication (R-20-scoped).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapWorkingOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/accounts/{id:guid}/orders", ReadAsync).RequireAuthorization()
            .WithTags("Orders")
            .WithSummary("Read the account's resting working orders from venue truth, optionally scoped to one instrument.");
        return endpoints;
    }

    internal static async Task<IResult> ReadAsync(
        Guid id,
        string? instrument,
        WorkingOrderReconciliationService reconciler,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        // `?instrument=` scopes the read to a single contract for the chart's per-instrument execution overlay
        // (gh#772). Two distinct client mistakes are 400s, never an empty book: a syntactically malformed symbol
        // (caught here), and a well-formed one the venue has no contract for (caught below, from the service). A
        // genuinely unreachable venue is neither -- it comes back Unknown with a 200.
        InstrumentId? scoped = null;
        if (instrument is not null)
        {
            if (!InstrumentId.TryParse(instrument, out InstrumentId parsed))
            {
                return Results.BadRequest(new { error = "instrument, when given, must be a valid instrument symbol." });
            }
            scoped = parsed;
        }

        // The clock is read here, at the boundary, and passed in -- the session decision stays pure.
        WorkingOrderReconciliation? result;
        try
        {
            result = await reconciler.ReconcileAsync(id, scoped, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (InstrumentNotResolvableException unresolvable)
        {
            return Results.BadRequest(new { error = unresolvable.Message });
        }

        if (result is null)
        {
            return Results.NotFound(); // not found or not owned by the caller (R-20)
        }

        // Match each venue order to the journaled Order.Id the write endpoints key on. The venue-truth read carries
        // the venue's own handle (VenueOrderKey); DELETE /orders/{id} and PATCH /orders/{id}/price route on {id:guid}
        // — the app Order.Id — so without this a caller cannot act on what it lists (it holds the venue key, not the
        // GUID the route needs). Owner-scoped by the DbContext query filter, and restricted to Working rows so a
        // terminal order that once held the key is never matched. A venue-spawned protective LEG has no Order row
        // (ADR-0007 — every protective leg the venue spawns is unjournaled), so it stays null and is not actionable
        // through /orders/{id}; the caller renders it accordingly rather than offering a control that cannot route.
        List<string> venueKeys = [.. result.Orders.Select(order => order.VenueOrderKey)];
        Dictionary<string, Guid> journaledIds = await database.Orders
            .Where(order => order.AccountId == id
                && order.Status == OrderStatus.Working
                && order.VenueOrderKey != null
                && venueKeys.Contains(order.VenueOrderKey))
            .Select(order => new { Key = order.VenueOrderKey!, order.Id })
            .ToDictionaryAsync(row => row.Key, row => row.Id, cancellationToken);

        // The PLATFORM-held staged-stop plan for each matched working order (ADR-0007, gh#865): the hidden working
        // stop the operator will move, and the safety → working → entry band a move must stay inside. Unlike the rest
        // of this read it is NOT venue truth — it never rests at the venue — so it is fetched from the local plan,
        // keyed to the order by Order.Id. Keying on the journaled ids keeps it Working-scoped for free: a terminal
        // order's id never enters `orderIds` (the join above is Working-only), so its plan is never surfaced — the
        // operator cannot move a stop on an order that is gone. One plan per order (unique index), so a key is unique.
        List<Guid> orderIds = [.. journaledIds.Values];
        Dictionary<Guid, WorkingStopGeometry> plans = (await database.StopPlans
            .Where(plan => orderIds.Contains(plan.OrderId))
            .Select(plan => new { plan.OrderId, plan.ActualStopPrice, plan.Staging, plan.SafetyStopPrice, plan.EntryPrice })
            .ToListAsync(cancellationToken))
            .ToDictionary(
                row => row.OrderId,
                row => new WorkingStopGeometry(row.ActualStopPrice, row.Staging, row.SafetyStopPrice, row.EntryPrice));

        return Results.Ok(new RestingOrdersResponse(
            result.Basis.ToString(),
            [.. result.Orders.Select(order =>
            {
                Guid? orderId = journaledIds.TryGetValue(order.VenueOrderKey, out Guid matched) ? matched : null;
                WorkingStopGeometry? plan = orderId is { } journaledId && plans.TryGetValue(journaledId, out WorkingStopGeometry? geometry)
                    ? geometry
                    : null;
                return MapRestingOrder(order, orderId, plan);
            })]));
    }

    /// <summary>
    /// Projects one venue-truth working order — with its journaled <c>Order.Id</c> and, when present, its
    /// PLATFORM-held staged-stop plan — into the wire DTO (gh#865). Pure and side-effect-free: the relational fetch
    /// happens in <see cref="ReadAsync"/>, so this shape assembly is unit-testable without a database.
    /// </summary>
    /// <param name="order">The venue-reported resting order.</param>
    /// <param name="orderId">The journaled <c>Order.Id</c> matched by venue key, or <see langword="null"/> for an unjournaled leg (gh#656).</param>
    /// <param name="plan">
    /// The order's staged-stop plan (ADR-0007), or <see langword="null"/> when it carries none. The four platform-held
    /// fields are present or absent as a whole — the working stop, its staging, and the safety / entry band surface
    /// together or not at all — because a working stop without its staging or band is not safely movable.
    /// </param>
    /// <returns>The resting-order DTO.</returns>
    internal static RestingOrder MapRestingOrder(WorkingOrder order, Guid? orderId, WorkingStopGeometry? plan) =>
        new(
            order.VenueOrderKey,
            orderId,
            order.Contract.Key,
            order.StopPrice?.Value,
            order.LimitPrice?.Value,
            order.Size,
            order.StopPrice is not null,
            plan?.WorkingStopPrice,
            plan?.Staging.ToString(),
            plan?.SafetyStopPrice,
            plan?.EntryPrice);
}

/// <summary>An account's resting orders from venue truth, and how far the read can be trusted (gh#381).</summary>
/// <param name="MarkBasis">
/// <c>Live</c>, <c>Settlement</c> (taken inside the maintenance window, so the venue's book may be
/// mid-transition), or <c>Unknown</c> (declared-unknown — <b>not</b> an empty book).
/// </param>
/// <param name="Orders">The venue-reported resting orders (empty when the basis is unknown).</param>
public sealed record RestingOrdersResponse(string MarkBasis, IReadOnlyList<RestingOrder> Orders);

/// <summary>One order resting at the venue.</summary>
/// <remarks>
/// Everything here is <b>venue truth</b> — what the venue reports standing on its book — <i>except</i> the four
/// trailing <c>*StopPrice</c> / <c>StopStaging</c> fields, which are <b>PLATFORM-held</b> (ADR-0007): the staged-stop
/// plan the platform holds for this order, and which does <b>not</b> rest at the venue. They are carried here (gh#865)
/// only so a move-stop UI can display the hidden working stop and pre-validate a move against its band; they are
/// <see langword="null"/> for any order with no <c>StopPlanRecord</c> (a bare entry, or a venue-spawned leg).
/// </remarks>
/// <param name="VenueOrderKey">The venue's own order handle.</param>
/// <param name="OrderId">
/// The journaled <c>Order.Id</c> the write endpoints (<c>DELETE /orders/{id}</c>, <c>PATCH /orders/{id}/price</c>)
/// key on, matched to this venue order by <see cref="VenueOrderKey"/> (gh#656). <see langword="null"/> for a
/// venue-spawned protective leg, which carries no <c>Order</c> row (ADR-0007) and so is not actionable through
/// <c>/orders/{id}</c> — the caller must not offer a cancel/modify control that cannot route.
/// </param>
/// <param name="Contract">The venue contract key.</param>
/// <param name="StopPrice">The stop trigger, when the order carries one.</param>
/// <param name="LimitPrice">The limit price, when the order carries one.</param>
/// <param name="Size">
/// How much the order covers. The reason gh#381 exists: a protective leg sized to less than the position it
/// guards leaves the remainder unprotected, and that was invisible through the app.
/// </param>
/// <param name="IsProtective">
/// Whether this leg is protective — it carries a stop trigger. Surfaced explicitly so a caller does not have to
/// re-derive the rule that a take-profit (limit-only) protects nothing about the loss.
/// </param>
/// <param name="WorkingStopPrice">
/// <b>PLATFORM-held</b> (ADR-0007), not venue truth. The hidden working stop (<c>StopPlanRecord.ActualStopPrice</c>) —
/// what the operator moves. <see langword="null"/> when the order carries no stop plan. The value the gh#267 write
/// (<c>PATCH /orders/{id}</c>'s <c>workingStopPrice</c>) re-stages, exposed here for read.
/// </param>
/// <param name="StopStaging">
/// <b>PLATFORM-held</b> (ADR-0007), not venue truth. Where the working stop rests (<c>StopPlanRecord.Staging</c>),
/// as its name — <c>Hidden</c>, <c>Native</c>, <c>Orphaned</c>, <c>Retired</c>. <b>LOAD-BEARING</b>: only <c>Hidden</c>
/// is locally movable; <c>Native</c> is a venue order and <c>Orphaned</c> re-arms on reconnect, so the UI gates the
/// move control on this. <see langword="null"/> when the order carries no stop plan.
/// </param>
/// <param name="SafetyStopPrice">
/// <b>PLATFORM-held</b> (ADR-0007), not venue truth. The catastrophic safety floor resting beyond the working stop
/// (<c>StopPlanRecord.SafetyStopPrice</c>), so the client can pre-validate the <c>safety → working → entry</c> band.
/// <see langword="null"/> when the order carries no stop plan.
/// </param>
/// <param name="EntryPrice">
/// <b>PLATFORM-held</b> (ADR-0007), not venue truth. The entry the stops are measured from
/// (<c>StopPlanRecord.EntryPrice</c>), the inner bound of the same band. <see langword="null"/> when the order
/// carries no stop plan.
/// </param>
public sealed record RestingOrder(
    string VenueOrderKey,
    Guid? OrderId,
    string Contract,
    decimal? StopPrice,
    decimal? LimitPrice,
    int Size,
    bool IsProtective,
    decimal? WorkingStopPrice = null,
    string? StopStaging = null,
    decimal? SafetyStopPrice = null,
    decimal? EntryPrice = null);

/// <summary>
/// The PLATFORM-held staged-stop geometry for one working order (ADR-0007, gh#865): the hidden working stop and the
/// <c>safety → working → entry</c> band it must stay inside. <b>None of this rests at the venue</b> — it is the local
/// plan, carried alongside the venue-truth order so a move-stop UI can display the working stop and pre-validate a
/// move. Present or absent as a whole: an order either has a <c>StopPlanRecord</c> or it does not.
/// </summary>
/// <param name="WorkingStopPrice">The hidden working stop (<c>StopPlanRecord.ActualStopPrice</c>) — what the operator moves.</param>
/// <param name="Staging">Where the working stop rests (<c>StopPlanRecord.Staging</c>); only <see cref="StopStaging.Hidden"/> is locally movable.</param>
/// <param name="SafetyStopPrice">The catastrophic safety floor beyond the working stop (<c>StopPlanRecord.SafetyStopPrice</c>).</param>
/// <param name="EntryPrice">The entry the stops are measured from (<c>StopPlanRecord.EntryPrice</c>).</param>
internal sealed record WorkingStopGeometry(
    decimal WorkingStopPrice,
    StopStaging Staging,
    decimal SafetyStopPrice,
    decimal EntryPrice);
