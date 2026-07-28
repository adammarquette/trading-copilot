using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        endpoints.MapGet("/accounts/{id:guid}/orders", ReadAsync).RequireAuthorization();
        return endpoints;
    }

    internal static async Task<IResult> ReadAsync(
        Guid id,
        WorkingOrderReconciliationService reconciler,
        CancellationToken cancellationToken)
    {
        // The clock is read here, at the boundary, and passed in -- the session decision stays pure.
        WorkingOrderReconciliation? result = await reconciler.ReconcileAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(); // not found or not owned by the caller (R-20)
        }

        return Results.Ok(new RestingOrdersResponse(
            result.Basis.ToString(),
            [.. result.Orders.Select(order => new RestingOrder(
                order.VenueOrderKey,
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
    string Contract,
    decimal? StopPrice,
    decimal? LimitPrice,
    int Size,
    bool IsProtective);
