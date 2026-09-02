using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Api.Journal;

/// <summary>
/// The day-realized / day-detail read surface (gh#1062, R-8/R-9) — <c>GET /accounts/{id}/journal/daily</c>, a
/// calendar of realized P&amp;L by Central trading day, and <c>GET /accounts/{id}/journal/daily/{date}</c>, the
/// operator's drill-down into one day's closed trades. Named in gh#20's own decomposition as "increment 2" and
/// never spun off — it is the sole <c>work:code</c> blocker on gh#659, the journal read surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuses <see cref="DailyRealizedReader"/> rather than a second definition.</b> That reader already answers
/// "today's realized P&amp;L" for the daily-headroom read (gh#587); <see cref="DailyRealizedReader.RealizedPnLByDayForAccountAsync"/>
/// and <see cref="DailyRealizedReader.TradesForDayForAccountAsync"/> generalize the same Central-day / realized-only /
/// R-14 mode-scoped rules from "today" to an arbitrary day or range, in the same file, sharing the Undeclared guard —
/// not a parallel implementation of realized P&amp;L.
/// </para>
/// <para>
/// <b>The mode is never a client input.</b> Both routes resolve the account's own <b>current</b> declared mode from
/// the database — mirroring <c>RiskEndpoints.GetHeadroomAsync</c> — and 404 when the account is absent or
/// <see cref="TradingMode.Undeclared"/> (gh#746 class of defect): an account that moved Practice → Live still carries
/// its practice rows in the journal, and letting a caller choose the mode (or silently reading every mode) would let
/// practice results blend into a live report (R-14). An Undeclared account trades nowhere, so it has nothing to
/// report — absence is the honest answer, never a fabricated empty-but-200 read.
/// </para>
/// <para>
/// <b>Tenancy is the DbContext's.</b> Every handler is an ordinary request path, so the automatic <c>IUserOwned</c>
/// default-deny filter (R-20 / ADR-0017) applies — a stranger's account is a <b>404</b>, never disclosed, and
/// another operator's trades never enter the sum.
/// </para>
/// <para>
/// <b>Read-only.</b> No new write surface; the account's journal is written elsewhere (<c>TradeJournalService</c>,
/// gh#731/gh#759).
/// </para>
/// </remarks>
public static class DailyJournalEndpoints
{
    /// <summary>Maps the day-realized / day-detail journal endpoints. All require authentication.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapDailyJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/accounts/{id:guid}/journal/daily")
            .RequireAuthorization()
            .WithTags("Journal");

        // The clock is read at the boundary and passed in, so the Central-day arithmetic stays testable.
        group.MapGet("/", (Guid id, DateOnly? from, DateOnly? to, TradingCopilotDbContext database, CancellationToken cancellationToken) =>
                GetDailyRealizedAsync(id, from, to, DateTimeOffset.UtcNow, database, cancellationToken))
            .WithSummary("A calendar of the account's realized P&L by Central trading day (R-9); defaults to the current Central month.");
        group.MapGet("/{date}", GetDayDetailAsync)
            .WithSummary("Drills into one Central trading day's closed, realized trades (R-8).");

        return endpoints;
    }

    /// <summary>
    /// Reads the account's realized P&amp;L by Central trading day (<c>GET /accounts/{id}/journal/daily</c>, R-9) over
    /// <paramref name="from"/>..<paramref name="to"/> (defaulting to the current Central month), scoped to the
    /// account's <b>current</b> mode (R-14).
    /// </summary>
    /// <param name="id">The account whose calendar to read.</param>
    /// <param name="from">The inclusive first Central day, or <see langword="null"/> for the start of the current Central month.</param>
    /// <param name="to">The inclusive last Central day, or <see langword="null"/> for the current Central day.</param>
    /// <param name="now">The current time, supplied by the caller — the default window derives from it.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The calendar (200), a 400 for an inverted range, or a 404 for an absent / Undeclared / foreign account.</returns>
    internal static async Task<IResult> GetDailyRealizedAsync(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        DateTimeOffset now,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        TradingMode? mode = await CurrentModeAsync(id, database, cancellationToken);
        if (mode is null or TradingMode.Undeclared)
        {
            return Results.NotFound();
        }

        DateOnly windowTo = to ?? DateOnly.FromDateTime(MarketClock.ToMarketTime(now).Date);
        DateOnly windowFrom = from ?? new DateOnly(windowTo.Year, windowTo.Month, 1);
        if (windowFrom > windowTo)
        {
            return Results.BadRequest(new { error = "from must not be after to." });
        }

        IReadOnlyList<DailyRealized> days = await database.RealizedPnLByDayForAccountAsync(
            id, mode.Value, windowFrom, windowTo, cancellationToken);

        return Results.Ok(new DailyRealizedPnLListResponse(
            [.. days.Select(day => new DailyRealizedPnLResponse(day.Date, day.RealizedPnL, day.TradeCount))]));
    }

    /// <summary>
    /// Drills into one Central trading day's closed, realized trades (<c>GET /accounts/{id}/journal/daily/{date}</c>,
    /// R-8), scoped to the account's <b>current</b> mode (R-14).
    /// </summary>
    /// <param name="id">The account whose day to read.</param>
    /// <param name="date">The Central trading day to read.</param>
    /// <param name="database">The scoped, R-20-filtered database.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The day's trades and their realized sum (200; zero trades on a quiet day, never absence), or a 404
    /// for an absent / Undeclared / foreign account.</returns>
    internal static async Task<IResult> GetDayDetailAsync(
        Guid id,
        DateOnly date,
        TradingCopilotDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        TradingMode? mode = await CurrentModeAsync(id, database, cancellationToken);
        if (mode is null or TradingMode.Undeclared)
        {
            return Results.NotFound();
        }

        IReadOnlyList<Trade> trades = await database.TradesForDayForAccountAsync(id, mode.Value, date, cancellationToken);
        decimal realized = trades.Sum(trade => trade.RealizedPnL!.Value);

        return Results.Ok(new DayDetailResponse(date, realized, [.. trades.Select(JournalTradeResponse.From)]));
    }

    /// <summary>
    /// The account's own <b>current</b> declared mode (R-14, gh#746) — never taken from a caller, and never the
    /// mode any individual trade was placed under. <see langword="null"/> when the account is absent or foreign
    /// (R-20); both that and <see cref="TradingMode.Undeclared"/> are the caller's cue to 404.
    /// </summary>
    private static Task<TradingMode?> CurrentModeAsync(Guid id, TradingCopilotDbContext database, CancellationToken cancellationToken) =>
        database.Accounts
            .Where(account => account.Id == id)
            .Select(account => (TradingMode?)account.Mode)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>One Central trading day's realized P&amp;L (gh#1062, R-9) — a row of the P&amp;L-by-day calendar.</summary>
/// <param name="Date">The Central trading day.</param>
/// <param name="RealizedPnL">The day's signed realized P&amp;L (positive net profit, negative net loss).</param>
/// <param name="TradeCount">How many realized trades closed that day.</param>
public sealed record DailyRealizedPnLResponse(DateOnly Date, decimal RealizedPnL, int TradeCount);

/// <summary>The P&amp;L-by-day calendar over a requested window (gh#1062, R-9).</summary>
/// <param name="Days">One entry per day that closed at least one realized trade; no entry for a quiet day.</param>
public sealed record DailyRealizedPnLListResponse(IReadOnlyList<DailyRealizedPnLResponse> Days);

/// <summary>One closed, realized trade for the day-detail drill-down (gh#1062, R-8).</summary>
/// <param name="Id">The trade's id.</param>
/// <param name="SuggestionId">The originating suggestion, when there was one.</param>
/// <param name="Instrument">The venue contract traded.</param>
/// <param name="Side">The entry side, as a name.</param>
/// <param name="Size">The size in contracts.</param>
/// <param name="EntryPrice">The average entry price.</param>
/// <param name="ExitPrice">The average exit price.</param>
/// <param name="RealizedPnL">The signed realized P&amp;L.</param>
/// <param name="ClosedAt">When the trade closed.</param>
public sealed record JournalTradeResponse(
    Guid Id,
    Guid? SuggestionId,
    string Instrument,
    string Side,
    int Size,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal RealizedPnL,
    DateTimeOffset ClosedAt)
{
    /// <summary>
    /// Projects a <see cref="Trade"/> to its response. Only ever called on rows <see cref="DailyRealizedReader"/>
    /// already filtered to realized-and-closed, so <see cref="Trade.ExitPrice"/> / <see cref="Trade.RealizedPnL"/> /
    /// <see cref="Trade.ClosedAt"/> are never null here.
    /// </summary>
    /// <param name="trade">The trade to project.</param>
    /// <returns>The response.</returns>
    public static JournalTradeResponse From(Trade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return new JournalTradeResponse(
            trade.Id,
            trade.SuggestionId,
            trade.Instrument,
            trade.Side.ToString(),
            trade.Size,
            trade.EntryPrice,
            trade.ExitPrice!.Value,
            trade.RealizedPnL!.Value,
            trade.ClosedAt!.Value);
    }
}

/// <summary>One Central trading day's drill-down (gh#1062, R-8) — the day-detail read.</summary>
/// <param name="Date">The Central trading day read.</param>
/// <param name="RealizedPnL">The day's signed realized P&amp;L sum; <c>0</c> on a quiet day, never absence.</param>
/// <param name="Trades">The day's closed, realized trades, oldest first.</param>
public sealed record DayDetailResponse(DateOnly Date, decimal RealizedPnL, IReadOnlyList<JournalTradeResponse> Trades);
