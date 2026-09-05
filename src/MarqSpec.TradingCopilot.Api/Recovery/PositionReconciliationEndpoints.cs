using MarqSpec.TradingCopilot.Domain;
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
            .WithSummary("Reconcile and read the account's open positions against venue truth, optionally scoped to one instrument.");
        // Same "Positions" group as the read on purpose: this is the write half of the same resource, and a new
        // tag would add a Scalar heading (and a GroupTags edit) for no reader benefit.
        endpoints.MapPost("/accounts/{id:guid}/positions/{instrument}/exit", ExitAsync).RequireAuthorization()
            .WithTags("Positions")
            .WithSummary("Close one instrument's position at market — the operator's per-position exit.");
        endpoints.MapPost("/accounts/{id:guid}/positions/{instrument}/reduce", ReduceAsync).RequireAuthorization()
            .WithTags("Positions")
            .WithSummary("Reduce one instrument's position by a number of contracts at market — a sized partial close.");
        return endpoints;
    }

    /// <summary>
    /// Closes one instrument's position (gh#656, R-11) — the blotter's exit control.
    /// </summary>
    /// <remarks>
    /// <b>Only a verified flat is a success.</b> A close the venue accepted while still reporting exposure, and a
    /// venue that could not be reached, both resolve to non-200: an operator told "done" stops watching a position
    /// that may still be live. A 409 is therefore "try again", not "it failed to close" — the position's state is
    /// exactly what the body reports.
    /// </remarks>
    internal static async Task<IResult> ExitAsync(
        Guid id,
        string instrument,
        PositionExitService exits,
        CancellationToken cancellationToken)
    {
        if (!InstrumentId.TryParse(instrument, out InstrumentId parsed))
        {
            // A client mistake, told apart from an unreachable venue: retrying a typo forever is a different
            // problem from retrying an outage.
            return Results.BadRequest(new { error = $"'{instrument}' is not a valid instrument." });
        }

        PositionExitResult? result = await exits.ExitAsync(id, parsed, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(); // not found / not owned (R-20)
        }

        return result.Outcome switch
        {
            PositionExitOutcome.Flat => Results.Ok(new PositionExitResponse(result.Outcome.ToString(), 0)),
            PositionExitOutcome.StillOpen => Results.Conflict(new PositionExitResponse(
                result.Outcome.ToString(), result.NetQuantity)),
            _ => Results.Conflict(new PositionExitResponse(PositionExitOutcome.Unreachable.ToString(), 0)),
        };
    }

    /// <summary>
    /// Reduces one instrument's position by a number of contracts (gh#928, R-11) — the blotter's reduce control.
    /// </summary>
    /// <remarks>
    /// <b>Only a verified reduction is a 200.</b> A partial close the venue accepted while still reporting the
    /// original size, one that took off a different amount than asked, a side flip, and an unreachable venue all
    /// resolve to non-200 — an operator told "reduced" would trust a smaller exposure than they hold. A reduce that
    /// is not strictly partial (at or beyond the open size) is a 400: a full close belongs to the exit control,
    /// which also cancels the protective legs.
    /// </remarks>
    internal static async Task<IResult> ReduceAsync(
        Guid id,
        string instrument,
        PositionReduceRequest request,
        PositionReduceService reduces,
        CancellationToken cancellationToken)
    {
        if (!InstrumentId.TryParse(instrument, out InstrumentId parsed))
        {
            return Results.BadRequest(new { error = $"'{instrument}' is not a valid instrument." });
        }

        if (request is null || request.Quantity <= 0)
        {
            // A non-positive reduce is meaningless, and a client mistake told apart from an unreachable venue.
            return Results.BadRequest(new { error = "quantity must be a positive number of contracts to reduce by." });
        }

        PositionReduceResult? result = await reduces.ReduceAsync(id, parsed, request.Quantity, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(); // not found / not owned (R-20)
        }

        return result.Outcome switch
        {
            PositionReduceOutcome.Reduced => Results.Ok(new PositionReduceResponse(
                result.Outcome.ToString(), result.NetQuantity)),
            // Not strictly partial (>= what is open): a client sizing error, distinct from a transient. The body
            // carries the current size so the operator can correct — or reach for the exit control for a full close.
            PositionReduceOutcome.ExceedsPosition => Results.BadRequest(new PositionReduceResponse(
                result.Outcome.ToString(), result.NetQuantity)),
            // Accepted-but-not-what-was-asked, or reversed: not done. A 409 is "re-check and try again", never
            // "it reduced".
            PositionReduceOutcome.NotReduced => Results.Conflict(new PositionReduceResponse(
                result.Outcome.ToString(), result.NetQuantity)),
            // Unreachable, and the fail-closed Unknown with it. The net quantity stays null rather than 0: an
            // exposure nobody could read is unknown, and a 0 here would let a client render an outage as flat.
            _ => Results.Conflict(new PositionReduceResponse(PositionReduceOutcome.Unreachable.ToString(), null)),
        };
    }

    internal static async Task<IResult> ReconcileAsync(
        Guid id,
        string? instrument,
        PositionReconciliationService reconciler,
        CancellationToken cancellationToken)
    {
        // `?instrument=` scopes the read to a single contract for the chart's per-instrument execution overlay
        // (gh#772). Two distinct client mistakes are 400s, never a flat account: a syntactically malformed symbol
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

        // The clock is read here, at the boundary, and passed in -- the settlement decision stays pure.
        PositionReconciliation? result;
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

/// <summary>The outcome of an operator-initiated exit (gh#656).</summary>
/// <param name="Outcome"><c>Flat</c>, <c>StillOpen</c> or <c>Unreachable</c> — as a name, not an integer.</param>
/// <param name="NetQuantity">The signed exposure the venue still reports; 0 when flat or unreachable.</param>
public sealed record PositionExitResponse(string Outcome, int NetQuantity);

/// <summary>How many contracts an operator wants to take off a position (gh#928).</summary>
/// <param name="Quantity">The positive number of contracts to reduce by; must be strictly less than what is open.</param>
public sealed record PositionReduceRequest(int Quantity);

/// <summary>The outcome of an operator-initiated reduce (gh#928).</summary>
/// <param name="Outcome"><c>Reduced</c>, <c>NotReduced</c>, <c>ExceedsPosition</c> or <c>Unreachable</c> — as a name, not an integer.</param>
/// <param name="NetQuantity">
/// The signed exposure the venue reports after the attempt — or the current size, when the request was refused
/// before the venue was touched. <b><c>null</c> when the venue could not be reached</b>: an exposure nobody could
/// read is unknown, and a <c>0</c> there would let a client render an outage as flat.
/// </param>
public sealed record PositionReduceResponse(string Outcome, int? NetQuantity);
