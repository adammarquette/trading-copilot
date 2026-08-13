using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Execution;
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

        return Results.Ok(new RestingOrdersResponse(
            result.Basis.ToString(),
            [.. result.Orders.Select(order => new RestingOrder(
                order.VenueOrderKey,
                journaledIds.TryGetValue(order.VenueOrderKey, out Guid orderId) ? orderId : null,
                order.Contract.Key,
                order.StopPrice?.Value,
                order.LimitPrice?.Value,
                order.Size,
                order.StopPrice is not null))]));
    }
}

/// <summary>An account's resting orders from venue truth, and how far the read can be trusted (gh#381).</summary>
/// <param name="MarkBasis">
/// <c>Live</c>, <c>Settlement</c> (taken inside the maintenance window, so the venue's book may be
/// mid-transition), or <c>Unknown</c> (declared-unknown — <b>not</b> an empty book).
/// </param>
/// <param name="Orders">The venue-reported resting orders (empty when the basis is unknown).</param>
public sealed record RestingOrdersResponse(string MarkBasis, IReadOnlyList<RestingOrder> Orders);

/// <summary>One order resting at the venue.</summary>
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
public sealed record RestingOrder(
    string VenueOrderKey,
    Guid? OrderId,
    string Contract,
    decimal? StopPrice,
    decimal? LimitPrice,
    int Size,
    bool IsProtective);
