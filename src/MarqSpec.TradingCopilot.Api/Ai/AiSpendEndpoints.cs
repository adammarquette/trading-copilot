using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The <c>/api/ai</c> read surface (gh#741): the operator's own AI spend, from the durable <c>AIUsage</c> ledger. The
/// spend section of Settings ("AI usage &amp; spend — your keys, your bill", gh#62) consumes it. Reads the ledger, not
/// the export-only meter (ADR-0002); owner-scoped (R-20, ADR-0015). It never composes or gates anything — a pure read.
/// </summary>
public static class AiSpendEndpoints
{
    /// <summary>Maps the AI-spend read endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAiSpendEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/ai").RequireAuthorization().WithTags("AI");
        // The clock is read at the boundary and passed in, so the Central-day arithmetic stays testable.
        group.MapGet("/spend", (
                DateTimeOffset? from,
                DateTimeOffset? to,
                TradingCopilotDbContext database,
                IOptions<GovernorOptions> governor,
                CancellationToken cancellationToken) =>
                GetSpendAsync(from, to, DateTimeOffset.UtcNow, database, governor.Value, cancellationToken))
            .WithSummary("The operator's AI spend over a period — total, by day, by model — with today's spend against the daily cap. Reads the AIUsage ledger, never the meter.");
        return endpoints;
    }

    /// <summary>
    /// Reads the operator's AI spend over <paramref name="from"/>..<paramref name="to"/> (defaulting to the current
    /// Central month), aggregated total / by model / by Central trading day, plus today's spend against the daily cap.
    /// </summary>
    /// <param name="from">The inclusive period start, or <see langword="null"/> for the start of the current Central month.</param>
    /// <param name="to">The inclusive period end, or <see langword="null"/> for <paramref name="now"/>.</param>
    /// <param name="now">The current time, supplied by the caller — the Central-day boundaries derive from it.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="governor">The spend-governor config carrying the daily cap.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The spend summary (200), or 400 when the period is inverted.</returns>
    internal static async Task<IResult> GetSpendAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        GovernorOptions governor,
        CancellationToken cancellationToken)
    {
        DateTimeOffset windowTo = to ?? now;
        DateTimeOffset windowFrom = from ?? CentralMonthStartUtc(now);
        if (windowFrom > windowTo)
        {
            return Results.BadRequest(new { error = "from must not be after to." });
        }

        // The operator's OWN decision spend over the period (R-20 owner-scoped) -- the "your bill" breakdown. It
        // deliberately excludes the deployment's SystemOwner embed-infra rows (data-dict §12), so it answers "what did
        // my decisions cost". That is a DIFFERENT question from "am I about to be paused", which `today` below answers
        // deployment-wide to match the cap -- the two are not the same figure even under one operator, because the
        // embed sentinel is a distinct owner. Reads the durable ledger, never the export-only meter (ADR-0002).
        var rows = await database.AiUsage
            .Where(record => record.OccurredAt >= windowFrom && record.OccurredAt <= windowTo)
            .Select(record => new { record.OccurredAt, record.Model, record.EstimatedCostUsd })
            .ToListAsync(cancellationToken);

        decimal total = rows.Sum(row => row.EstimatedCostUsd);

        List<AiSpendModelSlice> byModel = rows
            .GroupBy(row => row.Model)
            .Select(slice => new AiSpendModelSlice(slice.Key, slice.Sum(row => row.EstimatedCostUsd)))
            .OrderByDescending(slice => slice.CostUsd)
            .ThenBy(slice => slice.Model, StringComparer.Ordinal)
            .ToList();

        // Grouped by the CENTRAL trading day the daily cap resets on (MarketClock) -- a UTC date would split a live
        // CME session and disagree with what the cap actually enforces.
        List<AiSpendDaySlice> byDay = rows
            .GroupBy(row => DateOnly.FromDateTime(MarketClock.ToMarketTime(row.OccurredAt).Date))
            .Select(slice => new AiSpendDaySlice(slice.Key, slice.Sum(row => row.EstimatedCostUsd)))
            .OrderBy(slice => slice.Day)
            .ToList();

        // Today's spend against the cap, read the SAME way the governor ENFORCES it: DEPLOYMENT-WIDE
        // (IgnoreQueryFilters -- every owner, including the SystemOwner embed-infra rows), on the Central-day window.
        // The R-20 filter is crossed here for the same reason TriggerEvaluationService crosses it (gh#448): the cap is
        // a deployment cost, so "today vs cap" must be apples-to-apples with what actually pauses the co-pilot. An
        // owner-scoped figure would under-count the continuous embed spend and could read "under cap" while the
        // governor has already paused proposing -- the exact dishonesty this surface exists to avoid. Read
        // independently of the requested period so it is correct even when the period excludes today. The nullable
        // cast keeps an empty window a zero, matching the governor's own read.
        DateTimeOffset todayStart = MarketClock.CentralDayStartUtc(now);
        decimal today = await database.AiUsage
            .IgnoreQueryFilters()
            .Where(record => record.OccurredAt >= todayStart)
            .Select(record => (decimal?)record.EstimatedCostUsd)
            .SumAsync(cancellationToken) ?? 0m;

        return Results.Ok(new AiSpendResponse(
            windowFrom, windowTo, total, today, governor.DailyBudgetUsd, byModel, byDay));
    }

    // The first instant of the Central calendar month containing `now`, in UTC -- the default period (the wireframe's
    // month view). A display default local to this surface; the market-day boundary itself is MarketClock's.
    private static DateTimeOffset CentralMonthStartUtc(DateTimeOffset now)
    {
        DateTime central = MarketClock.ToMarketTime(now);
        DateTime firstOfMonth = DateTime.SpecifyKind(new DateTime(central.Year, central.Month, 1), DateTimeKind.Unspecified);
        return new DateTimeOffset(firstOfMonth, MarketClock.CentralTime.GetUtcOffset(firstOfMonth)).ToUniversalTime();
    }
}
