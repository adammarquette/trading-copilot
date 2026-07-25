using MarqSpec.TradingCopilot.Domain.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MarqSpec.TradingCopilot.Api.Kill;

/// <summary>
/// The kill-switch endpoints (gh#189, R-11, ADR-0007): the operator's process-wide panic control. Engaging
/// disables outbound orders, cancels working orders, and flattens-or-halts; disengaging re-enables outbound. All
/// require authentication and sit outside the per-account order routes — the kill switch is deployment-wide.
/// </summary>
public static class KillSwitchEndpoints
{
    /// <summary>Maps the kill-switch endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapKillSwitchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/kill-switch").RequireAuthorization();
        group.MapPost("/", EngageAsync);
        group.MapPost("/disengage", DisengageAsync);
        group.MapGet("/", GetState);

        return endpoints;
    }

    internal static async Task<IResult> EngageAsync(
        EngageKillSwitchRequest request,
        KillSwitchService service,
        CancellationToken cancellationToken)
    {
        // Hold-to-confirm (R-11): a panic action must not fire from a stray or accidental request. BOTH modes
        // require the explicit confirmation -- halt-only still cancels working orders (gh#189).
        if (!request.Confirmed)
        {
            return Results.UnprocessableEntity(new
            {
                error = "The kill switch requires explicit confirmation (hold-to-confirm). Re-send with confirmed = true.",
            });
        }

        KillSwitchReport report = await service.EngageAsync(request.Mode, request.Reason, DateTimeOffset.UtcNow, cancellationToken);

        return Results.Ok(new EngageKillSwitchResponse(report.Mode.ToString(), report.CancelledOrders, report.FlattenedPositions));
    }

    internal static async Task<IResult> DisengageAsync(KillSwitchService service, CancellationToken cancellationToken)
    {
        await service.DisengageAsync(DateTimeOffset.UtcNow, cancellationToken);

        return Results.Ok(new { engaged = false });
    }

    internal static IResult GetState(KillSwitch killSwitch)
    {
        KillSwitchStatus status = killSwitch.Status;

        return Results.Ok(new KillSwitchStateResponse(status.Engaged, status.Mode.ToString(), status.EngagedAt, status.Reason));
    }
}

/// <summary>A request to engage the kill switch (gh#189).</summary>
/// <param name="Mode">Flatten all open positions (default) or halt only (leave them on native safety stops).</param>
/// <param name="Confirmed">The hold-to-confirm gesture — must be <see langword="true"/> to engage.</param>
/// <param name="Reason">The operator's reason, optional, journaled with the action.</param>
public sealed record EngageKillSwitchRequest(KillSwitchMode Mode, bool Confirmed, string? Reason);

/// <summary>What engaging did (gh#189).</summary>
/// <param name="Mode">The mode applied to open positions.</param>
/// <param name="CancelledOrders">How many working orders were cancelled.</param>
/// <param name="FlattenedPositions">How many open positions were closed (0 in halt-only).</param>
public sealed record EngageKillSwitchResponse(string Mode, int CancelledOrders, int FlattenedPositions);

/// <summary>The current kill-switch state (gh#189).</summary>
/// <param name="Engaged">Whether outbound orders are currently disabled.</param>
/// <param name="Mode">What engaging does to open positions.</param>
/// <param name="EngagedAt">When it was engaged, or <see langword="null"/> when disengaged.</param>
/// <param name="Reason">The operator's reason, if one was given.</param>
public sealed record KillSwitchStateResponse(bool Engaged, string Mode, DateTimeOffset? EngagedAt, string? Reason);
