using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The <c>/api/triggers</c> endpoints (gh#385, A1) — the operator authors deterministic indicator triggers; the
/// scan evaluates exactly what is persisted. A request is validated <b>through the domain condition</b>
/// (<see cref="IndicatorThresholdCondition"/>, one source of truth) and refused whole on any violation. Operator-owned
/// (R-20): reads are scoped by the default-deny filter, writes stamp the caller as owner.
/// </summary>
public static class TriggerEndpoints
{
    /// <summary>Maps the trigger endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/triggers").RequireAuthorization();
        group.MapPost("/", CreateTriggerAsync);
        group.MapGet("/", ListTriggersAsync);
        group.MapGet("/{id:guid}", GetTriggerAsync);
        group.MapPatch("/{id:guid}", UpdateTriggerAsync);
        group.MapDelete("/{id:guid}", DeleteTriggerAsync);
        return endpoints;
    }

    internal static async Task<IResult> CreateTriggerAsync(
        TriggerRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        (IResult? error, IndicatorThresholdCondition? condition) = Validate(request);
        if (error is not null)
        {
            return error;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Trigger trigger = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            Instrument = condition!.Instrument.ToString(),
            Indicator = condition.Indicator,
            Period = condition.Period,
            ResolutionMinutes = condition.ResolutionMinutes,
            Comparison = condition.Comparison,
            Threshold = condition.Threshold,
            Route = request.Route,
            Enabled = request.Enabled,
            ArmState = TriggerArmState.Unseeded,
            ArmCycle = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        database.Triggers.Add(trigger);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/triggers/{trigger.Id}", TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> ListTriggersAsync(
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        List<Trigger> triggers = await database.Triggers
            .OrderByDescending(trigger => trigger.CreatedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(triggers.Select(TriggerResponse.From).ToList());
    }

    internal static async Task<IResult> GetTriggerAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Trigger? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return trigger is null ? Results.NotFound() : Results.Ok(TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> UpdateTriggerAsync(
        Guid id,
        TriggerRequest request,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        (IResult? error, IndicatorThresholdCondition? condition) = Validate(request);
        if (error is not null)
        {
            return error;
        }

        // The R-20 filter scopes this to the caller: another operator's trigger simply isn't found.
        Trigger? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (trigger is null)
        {
            return Results.NotFound();
        }

        trigger.Instrument = condition!.Instrument.ToString();
        trigger.Indicator = condition.Indicator;
        trigger.Period = condition.Period;
        trigger.ResolutionMinutes = condition.ResolutionMinutes;
        trigger.Comparison = condition.Comparison;
        trigger.Threshold = condition.Threshold;
        trigger.Route = request.Route;
        trigger.Enabled = request.Enabled;
        trigger.UpdatedAt = DateTimeOffset.UtcNow;

        // An edit re-seeds the debounce so a now-already-true condition does not fire on the next scan (the
        // cold-start rule). Bumping the cycle opens a fresh incident namespace, so a post-edit crossing never
        // collides with a pre-edit incident's dedup key.
        trigger.ArmState = TriggerArmState.Unseeded;
        trigger.ArmCycle++;

        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> DeleteTriggerAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        Trigger? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (trigger is null)
        {
            return Results.NotFound();
        }

        // Firing records survive deletion (soft reference, no cascade) — the journal outlives the trigger.
        database.Triggers.Remove(trigger);
        await database.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    // The persisted columns' limits (varchar(32), numeric(18,8) = 10 integer digits), enforced at the boundary so
    // an over-long symbol or an out-of-range threshold is a clean 400 rather than a 500 from a DB constraint.
    private const int MaxSymbolLength = 32;
    private const decimal ThresholdBound = 10_000_000_000m;

    private static (IResult? Error, IndicatorThresholdCondition? Condition) Validate(TriggerRequest request)
    {
        IndicatorThresholdCondition condition;
        try
        {
            condition = new IndicatorThresholdCondition(
                InstrumentId.Parse(request.Instrument), request.Indicator, request.Period, request.ResolutionMinutes,
                request.Comparison, request.Threshold);
        }
        catch (Exception validation) when (validation is ArgumentException or FormatException)
        {
            return (Results.BadRequest(new { error = validation.Message }), null);
        }

        if (condition.Instrument.ToString().Length > MaxSymbolLength || condition.Indicator.Length > MaxSymbolLength)
        {
            return (Results.BadRequest(new { error = $"Instrument and indicator must be at most {MaxSymbolLength} characters." }), null);
        }

        if (Math.Abs(condition.Threshold) >= ThresholdBound)
        {
            return (Results.BadRequest(new { error = "The threshold is out of range." }), null);
        }

        // Allowlist the route: MechanicalAlert is the only one inc-1 accepts.
        if (request.Route == TriggerRoute.AgentReview)
        {
            // Well-formed, but a capability inc-1 does not ship: the agent-review route + suggestion generation
            // are deferred (ADR-0008 — the LLM only at the edges). 422, not 400.
            return (Results.UnprocessableEntity(new
            {
                error = "The agent-review route is not yet available — this increment ships the mechanical-alert route only (ADR-0008).",
            }), null);
        }

        if (request.Route != TriggerRoute.MechanicalAlert)
        {
            // The unset zero, or an out-of-range integer bound past the enum.
            return (Results.BadRequest(new { error = "A valid route must be declared (MechanicalAlert)." }), null);
        }

        return (null, condition);
    }
}
