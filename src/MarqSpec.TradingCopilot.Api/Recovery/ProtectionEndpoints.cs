using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Recovery;

/// <summary>
/// The current synthetic-protection state (gh#222, R-11 / R-19) — the ground truth behind the safety strip's
/// degraded-protection indicator. Requires authentication.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a read exists at all, when the transitions are already broadcast.</b> The realtime channel cannot answer
/// "am I protected <i>right now</i>". <see cref="Realtime.RealtimeHub"/> replays a backlog only when the client
/// presents an <c>?after=</c> cursor, and a freshly loaded page has none — so an operator who reloads while
/// protection is degraded would receive no orphan event at all and the strip would render as protected. Same for a
/// client first opened after the drop, and same after a retention gap, where the gap message tells the client to
/// re-fetch over REST. So the durable answer is this read; the <c>protection.orphaned</c> /
/// <c>protection.restored</c> broadcasts are the <i>prompt</i> to take it again, not the state themselves.
/// </para>
/// <para>
/// Reading rather than folding the event payloads is also what makes a missed or replayed event harmless: there is
/// no client-side arithmetic to drift, and a historical event and a live one lead to the same re-read.
/// </para>
/// <para>
/// Owner-scoped, deliberately: it runs inside the R-20 default-deny filter, unlike
/// <see cref="OrphanGuardService"/>, which <c>IgnoreQueryFilters</c> because it is background plumbing acting for
/// the whole deployment. The two coincide in a single-operator deployment (ADR-0015/0017), and where they could
/// not, a user-facing read must show the caller their own protection.
/// </para>
/// </remarks>
public static class ProtectionEndpoints
{
    /// <summary>Maps the protection-state read. Requires authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapProtectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/protection")
            .RequireAuthorization()
            .WithTags("Protection")
            .MapGet("/", GetStateAsync)
            .WithSummary("Whether the operator's synthetic protection is currently degraded, and by how much.");

        return endpoints;
    }

    internal static async Task<IResult> GetStateAsync(
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        int orphaned = await database.StopPlans
            .CountAsync(plan => plan.Staging == StopStaging.Orphaned, cancellationToken);

        return Results.Ok(new ProtectionStateResponse(orphaned > 0, orphaned, NativeSafetyStopRemainsFloor: true));
    }
}

/// <summary>The operator's current synthetic-protection state (gh#222).</summary>
/// <param name="Degraded">
/// Whether any of the operator's hidden working stops are currently orphaned — their tighter protection cannot
/// promote until the venue connection is restored and each stop re-validates against venue truth.
/// </param>
/// <param name="OrphanedStops">How many are orphaned right now. Zero whenever <paramref name="Degraded"/> is false.</param>
/// <param name="NativeSafetyStopRemainsFloor">
/// Always <see langword="true"/>, and returned rather than assumed so the surface can say it without hard-coding a
/// safety claim of its own: R-19's distinction is that degraded is <b>never</b> unprotected. The native safety stop
/// stays with the position as the physical floor; only the operator's tighter synthetic stop is orphaned.
/// </param>
public sealed record ProtectionStateResponse(bool Degraded, int OrphanedStops, bool NativeSafetyStopRemainsFloor);
