using MarqSpec.TradingCopilot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The journaled-fills read (gh#792): the operator's fills on one account + instrument over a time window, for the
/// gh#727 chart fill-marker overlay.
/// </summary>
/// <remarks>
/// <para>
/// Grouped under <c>Orders</c> — a fill is an order's execution — alongside the venue-truth resting-orders read on
/// the same <c>/accounts/{id}</c> template. Read-only: no execution, no gate involvement, and (unlike the
/// reconcilers) no venue call at all — it reads the journal.
/// </para>
/// <para>
/// Owner-scoped (R-20 — a stranger's account is 404, existence undisclosed) and window-bounded (gh#644 — an
/// over-wide window is a 400, never a truncated overlay). Request-scoped, so the R-20 filter applies to both the
/// fills and the orders they join to.
/// </para>
/// </remarks>
public static class FillJournalEndpoints
{
    /// <summary>Maps the journaled-fills read. Requires authentication (R-20-scoped).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapFillEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/accounts/{id:guid}/fills", ReadAsync).RequireAuthorization()
            .WithTags("Orders")
            .WithSummary("Read the operator's journaled fills on an account for one instrument over a time window.");
        return endpoints;
    }

    internal static async Task<IResult> ReadAsync(
        Guid id,
        string? instrument,
        DateTimeOffset from,
        DateTimeOffset to,
        FillJournalReadService reader,
        CancellationToken cancellationToken)
    {
        // The overlay always charts ONE instrument, so the scope is required here (unlike the account-wide orders
        // read, where an absent instrument means "every contract"). A blank / malformed symbol is a client mistake.
        if (instrument is null || !InstrumentId.TryParse(instrument, out InstrumentId scoped))
        {
            return Results.BadRequest(new { error = "instrument is required and must be a valid instrument symbol." });
        }
        if (from >= to)
        {
            return Results.BadRequest(new { error = "the time window is empty: 'from' must be strictly before 'to'." });
        }

        FillJournalRead? result = await reader.ReadAsync(id, scoped, from, to, cancellationToken);
        if (result is null)
        {
            return Results.NotFound(); // not found or not owned by the caller (R-20)
        }
        if (result.WindowTooWide)
        {
            // Refuse an over-wide window rather than draw a truncated, misleading overlay (gh#644 discipline).
            return Results.BadRequest(new
            {
                error = $"the window returns more than {FillJournalReadService.MaxFills} fills; narrow it.",
            });
        }

        return Results.Ok(new FillsResponse([.. result.Fills]));
    }
}

/// <summary>The operator's journaled fills on an instrument over a window (gh#792).</summary>
/// <param name="Fills">The fills in the window, ascending by execution time.</param>
public sealed record FillsResponse(IReadOnlyList<ExecutedFill> Fills);
