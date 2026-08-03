using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The position-reconcile endpoint (gh#193): reports an account's positions from <b>venue truth</b>, tagged with
/// their mark basis, so a settlement re-mark is never presented as live movement and an unreachable venue reads
/// as declared-unknown (R-13, R-19, ADR-0013).
/// </summary>
public static class PositionReconciliationEndpoints
{
    /// <summary>Maps the position-reconcile endpoint. Requires authentication (R-20-scoped).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapPositionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/accounts/{id:guid}/positions", ReconcileAsync).RequireAuthorization()
            .WithTags("Positions")
            .WithSummary("Reconcile and read the account's open positions against venue truth.");
        return endpoints;
    }

    internal static async Task<IResult> ReconcileAsync(
        Guid id,
        PositionReconciliationService reconciler,
        CancellationToken cancellationToken)
    {
        // The clock is read here, at the boundary, and passed in -- the settlement decision stays pure.
        PositionReconciliation? result = await reconciler.ReconcileAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(); // not found or not owned by the caller (R-20)
        }

        return Results.Ok(new ReconciledPositionsResponse(
            result.Basis.ToString(),
            [.. result.Positions.Select(position => new ReconciledPosition(
                position.Contract.Key, position.NetQuantity, position.AveragePrice.Value, position.IsFlat))]));
    }
}

/// <summary>An account's reconciled positions and the basis their marks rest on (gh#193).</summary>
/// <param name="MarkBasis"><c>Live</c>, <c>Settlement</c> (a re-mark, not live movement), or <c>Unknown</c> (declared-unknown).</param>
/// <param name="Positions">The venue-reported positions (empty when the basis is unknown).</param>
public sealed record ReconciledPositionsResponse(string MarkBasis, IReadOnlyList<ReconciledPosition> Positions);

/// <summary>One reconciled position from venue truth.</summary>
/// <param name="Contract">The venue contract key.</param>
/// <param name="NetQuantity">Signed net quantity: positive long, negative short, zero flat.</param>
/// <param name="AveragePrice">The position's average entry price.</param>
/// <param name="IsFlat">Whether the position is flat.</param>
public sealed record ReconciledPosition(string Contract, int NetQuantity, decimal AveragePrice, bool IsFlat);
