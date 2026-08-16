using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Domain.Flatten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Ai;

/// <summary>
/// The <c>/api/ai/attribution</c> read surface (gh#767) — <b>per-suggestion</b> and <b>per-taken-trade</b> AI cost,
/// built on the <c>AIUsage</c> ledger's firing correlation (gh#767 increment A). It answers the question aggregate
/// spend cannot — <i>is a suggestion worth what it cost?</i> — by summing each firing's cost against the suggestion it
/// produced and, through <c>Trade.SuggestionId</c>, the trade actually taken. Owner-scoped (R-20, ADR-0015); reads the
/// durable ledger, never the export-only meter (ADR-0002). A pure read — it composes nothing and gates nothing.
/// </summary>
public static class AiAttributionEndpoints
{
    /// <summary>Maps the AI-attribution read endpoint. Requires authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAiAttributionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ai/attribution", (
                DateTimeOffset? from,
                DateTimeOffset? to,
                TradingCopilotDbContext database,
                CancellationToken cancellationToken) =>
                GetAttributionAsync(from, to, DateTimeOffset.UtcNow, database, cancellationToken))
            .RequireAuthorization()
            .WithTags("AI")
            .WithSummary("Per-suggestion and per-taken-trade AI cost over a period, plus the unattributed spend of billed reviews that produced no suggestion. Reads the AIUsage ledger's firing correlation (gh#767), never the meter.");
        return endpoints;
    }

    /// <summary>
    /// Reads the operator's per-suggestion and per-taken-trade AI cost over <paramref name="from"/>..<paramref name="to"/>
    /// (defaulting to the current Central month), plus the unattributed spend of billed reviews that staged no suggestion.
    /// </summary>
    /// <param name="from">The inclusive period start, or <see langword="null"/> for the start of the current Central month.</param>
    /// <param name="to">The inclusive period end, or <see langword="null"/> for <paramref name="now"/>.</param>
    /// <param name="now">The current time, supplied by the caller — the Central-month boundary derives from it.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The attribution summary (200), or 400 when the period is inverted.</returns>
    internal static async Task<IResult> GetAttributionAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        DateTimeOffset windowTo = to ?? now;
        DateTimeOffset windowFrom = from ?? CentralMonthStartUtc(now);
        if (windowFrom > windowTo)
        {
            return Results.BadRequest(new { error = "from must not be after to." });
        }

        // The operator's own firing-correlated agent-review cost rows (R-20). NOT period-bounded on the read: a
        // suggestion's total cost is the sum of ALL its firing's rows, and a trade can close in-window off a suggestion
        // that fired before it — so bounding here would under-count. Grouped by firing → (cost, call count). A single
        // operator's agent-review history is modest; a windowed pre-filter is a future refinement if it grows.
        var costRows = await database.AiUsage
            .Where(record => record.TriggerFiringId != null)
            .Select(record => new { Firing = record.TriggerFiringId!.Value, record.EstimatedCostUsd, record.OccurredAt })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, (decimal Cost, int Calls)> costByFiring = costRows
            .GroupBy(row => row.Firing)
            .ToDictionary(group => group.Key, group => (group.Sum(row => row.EstimatedCostUsd), group.Count()));

        // The operator's suggestions carrying a firing (R-20): the CreatedAt/instrument metadata for the per-suggestion
        // read, plus the suggestion-id → firing map a taken trade joins through (its suggestion may pre-date the window).
        var suggestions = await database.Suggestions
            .Where(suggestion => suggestion.TriggerFiringId != null)
            .Select(suggestion => new
            {
                suggestion.Id,
                suggestion.Instrument,
                suggestion.Side,
                suggestion.CreatedAt,
                Firing = suggestion.TriggerFiringId!.Value,
            })
            .ToListAsync(cancellationToken);

        HashSet<Guid> attributedFirings = suggestions.Select(suggestion => suggestion.Firing).ToHashSet();
        Dictionary<Guid, Guid> firingBySuggestion = suggestions
            .GroupBy(suggestion => suggestion.Id)
            .ToDictionary(group => group.Key, group => group.First().Firing);

        // Per-suggestion cost: suggestions CREATED in the window, each with its firing's total cost + call count (>1 = a
        // triage→deep escalation, gh#449, so its value is measurable).
        List<AiSuggestionCost> perSuggestion = suggestions
            .Where(suggestion => suggestion.CreatedAt >= windowFrom && suggestion.CreatedAt <= windowTo)
            .Select(suggestion =>
            {
                costByFiring.TryGetValue(suggestion.Firing, out (decimal Cost, int Calls) tally);
                return new AiSuggestionCost(
                    suggestion.Id, suggestion.Instrument, suggestion.Side.ToString(),
                    tally.Cost, tally.Calls, tally.Calls > 1, suggestion.CreatedAt);
            })
            .OrderByDescending(cost => cost.CostUsd)
            .ThenByDescending(cost => cost.CreatedAt)
            .ToList();

        // Per-taken-trade cost: trades CLOSED in the window that came from a suggestion, each reporting that suggestion's
        // AI cost (Trade.SuggestionId → Suggestion.TriggerFiringId → Σ cost) against the trade's realized outcome.
        var trades = await database.Trades
            .Where(trade => trade.SuggestionId != null && trade.ClosedAt != null
                && trade.ClosedAt >= windowFrom && trade.ClosedAt <= windowTo)
            .Select(trade => new
            {
                trade.Id,
                trade.Instrument,
                trade.RealizedPnL,
                trade.ClosedAt,
                SuggestionId = trade.SuggestionId!.Value,
            })
            .ToListAsync(cancellationToken);

        List<AiTradeCost> takenTrades = trades
            .Select(trade =>
            {
                decimal cost = 0m;
                if (firingBySuggestion.TryGetValue(trade.SuggestionId, out Guid firing))
                {
                    costByFiring.TryGetValue(firing, out (decimal Cost, int Calls) tally);
                    cost = tally.Cost;
                }

                return new AiTradeCost(trade.Id, trade.Instrument, trade.RealizedPnL, cost, trade.ClosedAt);
            })
            .OrderByDescending(trade => trade.ClosedAt)
            .ToList();

        // Unattributed: firing-correlated cost that OCCURRED in the window whose firing produced no suggestion (a billed
        // review that was suppressed / geometry-rejected / throttled). Surfaced, never dropped — Σ(per-suggestion) is a
        // floor on agent-review spend and this is exactly the gap (gh#767 review).
        decimal unattributed = costRows
            .Where(row => row.OccurredAt >= windowFrom && row.OccurredAt <= windowTo && !attributedFirings.Contains(row.Firing))
            .Sum(row => row.EstimatedCostUsd);

        return Results.Ok(new AiAttributionResponse(windowFrom, windowTo, perSuggestion, takenTrades, unattributed));
    }

    // The first instant of the Central calendar month containing `now`, in UTC — the default period, matching the
    // aggregate spend surface (gh#741). The market-day boundary itself is MarketClock's.
    private static DateTimeOffset CentralMonthStartUtc(DateTimeOffset now)
    {
        DateTime central = MarketClock.ToMarketTime(now);
        DateTime firstOfMonth = DateTime.SpecifyKind(new DateTime(central.Year, central.Month, 1), DateTimeKind.Unspecified);
        return new DateTimeOffset(firstOfMonth, MarketClock.CentralTime.GetUtcOffset(firstOfMonth)).ToUniversalTime();
    }
}

/// <summary>
/// The operator's per-suggestion and per-taken-trade AI cost over a period (gh#767) — what each decision cost, so
/// <i>is a suggestion worth what it cost?</i> is answerable, not just <i>am I near the cap?</i>.
/// </summary>
/// <param name="From">The inclusive period start (as resolved).</param>
/// <param name="To">The inclusive period end (as resolved).</param>
/// <param name="Suggestions">Per-suggestion cost for suggestions created in the period, dearest first.</param>
/// <param name="TakenTrades">Per-taken-trade cost for trades closed in the period, most-recently-closed first.</param>
/// <param name="UnattributedUsd">Spend on billed reviews in the period that produced no suggestion (the orphan-firing bucket).</param>
public sealed record AiAttributionResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<AiSuggestionCost> Suggestions,
    IReadOnlyList<AiTradeCost> TakenTrades,
    decimal UnattributedUsd);

/// <summary>One suggestion's total AI cost — the sum of its firing's rows (a triage call, plus a deep call when it escalated).</summary>
/// <param name="SuggestionId">The suggestion.</param>
/// <param name="Instrument">The suggestion's instrument.</param>
/// <param name="Side">The proposed side, as a name.</param>
/// <param name="CostUsd">The total estimated AI cost of the calls that produced it.</param>
/// <param name="Calls">How many billed model calls its firing made (1 = single-pass, 2 = triage→deep escalation).</param>
/// <param name="Escalated">Whether the review escalated to the deep tier (gh#449) — <c>Calls &gt; 1</c>.</param>
/// <param name="CreatedAt">When the suggestion was issued.</param>
public sealed record AiSuggestionCost(
    Guid SuggestionId,
    string Instrument,
    string Side,
    decimal CostUsd,
    int Calls,
    bool Escalated,
    DateTimeOffset CreatedAt);

/// <summary>A taken (closed) trade and the AI cost of the suggestion that produced it — cost against realized outcome.</summary>
/// <param name="TradeId">The trade.</param>
/// <param name="Instrument">The trade's instrument.</param>
/// <param name="RealizedPnL">The trade's realized P&amp;L (present for a closed trade).</param>
/// <param name="SuggestionCostUsd">The total AI cost of the suggestion that produced the trade.</param>
/// <param name="ClosedAt">When the trade closed.</param>
public sealed record AiTradeCost(
    Guid TradeId,
    string Instrument,
    decimal? RealizedPnL,
    decimal SuggestionCostUsd,
    DateTimeOffset? ClosedAt);
