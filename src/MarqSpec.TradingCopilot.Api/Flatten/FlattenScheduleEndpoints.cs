using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Flatten;

/// <summary>
/// The auto-flatten schedule read (gh#657, R-13): the next deadline per governed market, for the operator's
/// always-visible countdown. Requires authentication — this is the deployment's safety configuration, and it
/// names the exact time positions stop being the operator's to manage.
/// </summary>
/// <remarks>
/// <para>
/// Read-only. Auto-flatten is the one autonomous action in the system and it only ever reduces exposure; nothing
/// here schedules, arms or disarms it. The schedule is configuration (<see cref="FlattenOptions"/>), changed by
/// deploying different configuration and reported at startup by <see cref="FlattenScheduleReporter"/>.
/// </para>
/// <para>
/// Until this existed the schedule was reachable only through that startup log, which is invisible to the SPA.
/// The countdown is the one element of the safety strip that genuinely cannot be computed client-side: the
/// deadline is a wall-clock Central time, so a browser resolving it against its own zone is an hour out for the
/// weeks around a daylight-saving change — and wrong in the direction that displays a deadline the scheduler will
/// not act on.
/// </para>
/// </remarks>
public static class FlattenScheduleEndpoints
{
    /// <summary>Maps the flatten-schedule read. Requires authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapFlattenScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/flatten")
            .RequireAuthorization()
            .WithTags("Flatten")
            .MapGet("/schedule", GetSchedule)
            .WithSummary("The next auto-flatten deadline per governed market, with the server's own instant.");

        return endpoints;
    }

    internal static IResult GetSchedule(IOptions<FlattenOptions> options, TimeProvider clock)
    {
        DateTimeOffset asOf = clock.GetUtcNow();

        FlattenMarketDeadline[] markets =
        [
            .. options.Value.DescribeNext(asOf)
                .OrderBy(report => report.DeadlineUtc)
                .ThenBy(report => report.Instrument.Symbol, StringComparer.Ordinal)
                .Select(report => new FlattenMarketDeadline(
                    report.Instrument.Symbol,
                    report.Deadline.ToString("HH:mm", CultureInfo.InvariantCulture),
                    report.DeadlineUtc,
                    report.Enabled,
                    report.Source.ToString())),
        ];

        return Results.Ok(new FlattenScheduleResponse(asOf, markets));
    }
}

/// <summary>The auto-flatten schedule as the client sees it (gh#657, R-13).</summary>
/// <param name="AsOf">
/// The server's own instant, stamped as the schedule was read. The client counts down
/// <c>DeadlineUtc - AsOf</c> rather than measuring against its own clock, so a skewed workstation cannot invent
/// safety margin that does not exist.
/// </param>
/// <param name="Markets">One entry per governed market, soonest deadline first.</param>
public sealed record FlattenScheduleResponse(DateTimeOffset AsOf, IReadOnlyList<FlattenMarketDeadline> Markets);

/// <summary>One market's next auto-flatten deadline (gh#657, R-13).</summary>
/// <param name="Instrument">The market symbol.</param>
/// <param name="Deadline">The deadline in market wall-clock (Central) time, <c>HH:mm</c> — what the rulebook says.</param>
/// <param name="DeadlineUtc">The next occurrence of that deadline as an instant, at or after <c>AsOf</c>.</param>
/// <param name="Enabled">
/// Whether auto-flatten is armed here. A market with this <see langword="false"/> is still returned: it is R-13's
/// deliberate, warned override, and the operator must see it as unarmed rather than not see it at all.
/// </param>
/// <param name="Source">Whether the deadline came from configuration or the built-in default.</param>
public sealed record FlattenMarketDeadline(
    string Instrument,
    string Deadline,
    DateTimeOffset DeadlineUtc,
    bool Enabled,
    string Source);
