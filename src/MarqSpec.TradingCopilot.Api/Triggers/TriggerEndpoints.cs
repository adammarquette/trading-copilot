using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain;
using MarqSpec.TradingCopilot.Domain.MarketData;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Triggers;

/// <summary>
/// The <c>/api/triggers</c> endpoints (gh#385, R-4 / R-7, ADR-0008) — the operator authors and manages standing
/// deterministic triggers; the scan evaluates exactly what is persisted. Every write is validated at the boundary
/// (a corrupt enum, a non-positive period, an unknown indicator, the not-yet-available agent-review route are all
/// refused) so a malformed trigger never reaches the scan or the database check constraints.
/// </summary>
public static class TriggerEndpoints
{
    // The R-22 indicator names a trigger may name (AtrIndicator / RsiIndicator). Case-insensitive in, canonical out,
    // so the stored name is exactly the IIndicatorSource read identity.
    private static readonly IReadOnlyDictionary<string, string> _knownIndicators =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AtrIndicator.IndicatorName] = AtrIndicator.IndicatorName,
            [RsiIndicator.IndicatorName] = RsiIndicator.IndicatorName,
        };

    /// <summary>Maps the trigger endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/triggers").RequireAuthorization();
        group.MapPost("/", CreateTriggerAsync);
        group.MapGet("/", ListTriggersAsync);
        group.MapGet("/{id:guid}", GetTriggerAsync);
        group.MapPatch("/{id:guid}", PatchTriggerAsync);
        group.MapDelete("/{id:guid}", DeleteTriggerAsync);
        return endpoints;
    }

    internal static async Task<IResult> CreateTriggerAsync(
        CreateTriggerRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        if (!InstrumentId.TryParse(request.Symbol, out InstrumentId instrument))
        {
            return Results.BadRequest(new { error = "A trigger needs a non-blank instrument symbol." });
        }

        if (request.Comparison == IndicatorComparison.Unknown)
        {
            return Results.BadRequest(new { error = "The comparison must be Below or Above." });
        }

        if (request.Route == TriggerRoute.Unknown)
        {
            return Results.BadRequest(new { error = "The route must be Mechanical (agent-review is not yet available)." });
        }

        if (request.Route == TriggerRoute.AgentReview)
        {
            return Results.UnprocessableEntity(new { error = "The agent-review route is not yet available." });
        }

        if (request.Period <= 0)
        {
            return Results.BadRequest(new { error = "The period must be positive." });
        }

        if (request.ResolutionMinutes <= 0)
        {
            return Results.BadRequest(new { error = "The resolution must be a positive number of minutes." });
        }

        if (string.IsNullOrWhiteSpace(request.Indicator)
            || !_knownIndicators.TryGetValue(request.Indicator, out string? indicatorName))
        {
            return Results.BadRequest(new
            {
                error = $"Unknown indicator — supported names are {string.Join(", ", _knownIndicators.Keys)}.",
            });
        }

        if (request.Hysteresis is <= 0m)
        {
            return Results.BadRequest(new { error = "The hysteresis band must be positive when set — null means none." });
        }

        TriggerRecord trigger = new()
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId,
            Symbol = instrument.Symbol,
            Indicator = indicatorName,
            Period = request.Period,
            ResolutionMinutes = request.ResolutionMinutes,
            ConditionKind = TriggerConditionKind.IndicatorThreshold,
            Comparison = request.Comparison,
            Threshold = request.Threshold,
            Hysteresis = request.Hysteresis,
            Route = request.Route,
            Severity = request.Severity,
            Enabled = true,
            ArmState = TriggerArmState.Unseeded,
            ArmCycle = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        database.Triggers.Add(trigger);
        await database.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/triggers/{trigger.Id}", TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> ListTriggersAsync(
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        // The default-deny R-20 filter scopes this to the caller's triggers -- no explicit owner predicate needed.
        List<TriggerRecord> triggers = await database.Triggers
            .OrderBy(trigger => trigger.CreatedAt)
            .ToListAsync(cancellationToken);
        return Results.Ok(triggers.Select(TriggerResponse.From).ToList());
    }

    internal static async Task<IResult> GetTriggerAsync(
        Guid id,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        TriggerRecord? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        // The R-20 filter has already hidden another operator's trigger, so "not visible" reads as 404.
        return trigger is null ? Results.NotFound() : Results.Ok(TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> PatchTriggerAsync(
        Guid id,
        PatchTriggerRequest request,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        _ = currentUser; // ownership is enforced by the R-20 filter below; the parameter documents the intent.

        TriggerRecord? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (trigger is null)
        {
            return Results.NotFound();
        }

        if (request.Comparison is { } comparison && comparison == IndicatorComparison.Unknown)
        {
            return Results.BadRequest(new { error = "The comparison must be Below or Above." });
        }

        if (request.Hysteresis is <= 0m)
        {
            return Results.BadRequest(new { error = "The hysteresis band must be positive when set — null means none." });
        }

        if (request.Enabled is { } enabled)
        {
            trigger.Enabled = enabled;
        }

        if (request.Threshold is { } threshold)
        {
            trigger.Threshold = threshold;
        }

        if (request.Hysteresis is { } hysteresis)
        {
            trigger.Hysteresis = hysteresis;
        }

        if (request.Comparison is { } newComparison)
        {
            trigger.Comparison = newComparison;
        }

        if (request.Severity is { } severity)
        {
            trigger.Severity = severity;
        }

        // A condition that became true while disabled or under an old definition must re-seed SILENTLY, not fire on
        // re-enable -- so any edit resets the debounce to Unseeded. Bump the incident cycle too: a currently-fired
        // trigger has an OPEN dedup incident under its current cycle key, and without a fresh cycle the next genuine
        // crossing would mint that same key and be SUPPRESSED as a duplicate -- a silent miss, with a firing row
        // that lies it fired. The counter only ever moves forward; the firing history is the journal's, not the
        // request's. (The old incident lingers open until it ages out; it is a tell-once Notify, not a nagging Page.)
        trigger.ArmState = TriggerArmState.Unseeded;
        trigger.ArmCycle++;
        await database.SaveChangesAsync(cancellationToken);

        return Results.Ok(TriggerResponse.From(trigger));
    }

    internal static async Task<IResult> DeleteTriggerAsync(
        Guid id,
        ICurrentUser currentUser,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        _ = currentUser; // ownership is enforced by the R-20 filter below; the parameter documents the intent.

        TriggerRecord? trigger = await database.Triggers
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (trigger is null)
        {
            return Results.NotFound();
        }

        database.Triggers.Remove(trigger);
        await database.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>The body of a create-trigger request (gh#385). Only the indicator-threshold kind is authored today.</summary>
/// <param name="Symbol">The venue-neutral instrument symbol (e.g. <c>ES</c>).</param>
/// <param name="Indicator">The R-22 indicator name (<c>atr</c> / <c>rsi</c>), case-insensitive.</param>
/// <param name="Period">The indicator period; must be positive.</param>
/// <param name="ResolutionMinutes">The bar size in minutes; must be positive.</param>
/// <param name="Comparison">Below or Above; Unknown is refused.</param>
/// <param name="Threshold">The threshold to compare against.</param>
/// <param name="Route">Where a fire routes; only Mechanical is available.</param>
/// <param name="Hysteresis">The optional re-arm dead-band; null means none, and it must be positive when set.</param>
/// <param name="Severity">How loudly the alert arrives; defaults to <see cref="NotificationSeverity.Notify"/>.</param>
public sealed record CreateTriggerRequest(
    string Symbol,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    IndicatorComparison Comparison,
    decimal Threshold,
    TriggerRoute Route,
    decimal? Hysteresis = null,
    NotificationSeverity Severity = NotificationSeverity.Notify);

/// <summary>
/// The body of a patch-trigger request (gh#385). Every field is optional — null means "leave unchanged". The
/// debounce state (<c>ArmState</c>, <c>ArmCycle</c>, <c>LastFiredAt</c>, <c>LastEvaluatedValue</c>) is deliberately
/// absent: it is the scan's and the journal's, never the caller's, and any edit re-seeds the arm state silently.
/// </summary>
/// <param name="Enabled">Enable or disable the trigger.</param>
/// <param name="Threshold">A new threshold.</param>
/// <param name="Hysteresis">A new re-arm dead-band; must be positive when set (clearing it is not offered here).</param>
/// <param name="Comparison">A new comparison; Unknown is refused.</param>
/// <param name="Severity">A new alert severity.</param>
public sealed record PatchTriggerRequest(
    bool? Enabled = null,
    decimal? Threshold = null,
    decimal? Hysteresis = null,
    IndicatorComparison? Comparison = null,
    NotificationSeverity? Severity = null);

/// <summary>The trigger as returned by the API (gh#385) — the persisted state, including the debounce memory.</summary>
/// <param name="Id">The trigger id.</param>
/// <param name="Symbol">The instrument symbol.</param>
/// <param name="Indicator">The indicator name.</param>
/// <param name="Period">The indicator period.</param>
/// <param name="ResolutionMinutes">The bar size in minutes.</param>
/// <param name="Comparison">The comparison.</param>
/// <param name="Threshold">The threshold.</param>
/// <param name="Hysteresis">The re-arm dead-band, if any.</param>
/// <param name="Route">Where a fire routes.</param>
/// <param name="Severity">The alert severity.</param>
/// <param name="Enabled">Whether the scan evaluates it.</param>
/// <param name="ArmState">The debounce state.</param>
/// <param name="ArmCycle">The incident counter.</param>
/// <param name="LastEvaluatedValue">The last measured value, if any.</param>
/// <param name="LastFiredAt">When it last fired, if ever.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record TriggerResponse(
    Guid Id,
    string Symbol,
    string Indicator,
    int Period,
    int ResolutionMinutes,
    IndicatorComparison Comparison,
    decimal Threshold,
    decimal? Hysteresis,
    TriggerRoute Route,
    NotificationSeverity Severity,
    bool Enabled,
    TriggerArmState ArmState,
    int ArmCycle,
    decimal? LastEvaluatedValue,
    DateTimeOffset? LastFiredAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a persisted trigger into its API representation.</summary>
    /// <param name="record">The persisted trigger.</param>
    /// <returns>The response DTO.</returns>
    public static TriggerResponse From(TriggerRecord record) => new(
        record.Id,
        record.Symbol,
        record.Indicator,
        record.Period,
        record.ResolutionMinutes,
        record.Comparison,
        record.Threshold,
        record.Hysteresis,
        record.Route,
        record.Severity,
        record.Enabled,
        record.ArmState,
        record.ArmCycle,
        record.LastEvaluatedValue,
        record.LastFiredAt,
        record.CreatedAt);
}
