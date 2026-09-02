using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Reads an account's <b>realized</b> P&amp;L for the current trading day (gh#587) — the consumed input the
/// daily-headroom read surface projects the declared limits over, computed from stored closed trades rather than a
/// send-path composition.
/// </summary>
public static class DailyRealizedReader
{
    /// <summary>
    /// Sums the account's realized P&amp;L for the <b>CME/Central trading day</b> containing <paramref name="now"/>,
    /// counting <b>only</b> trades taken under <paramref name="mode"/> (R-14, gh#746).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The day is the US-Central calendar day</b> (midnight→midnight), the boundary the gh#380 consistency window
    /// already uses (<see cref="MarketClock"/>) — a UTC date would split a CME session and halve the day. For the
    /// flat-before-close day-trading workflow this <b>equals</b> the venue's session day; the two diverge only for
    /// <b>evening / overnight</b> trades (the prop firm's session resets ~5pm CT), so an evening loss would attribute
    /// to the prior calendar day here while the venue counts it against the new session. Reconciling the gate's own
    /// <i>deferred</i> day-realized (gh#11 — currently a hardcoded 0, to be sourced later) to this same convention is
    /// where the daily boundary must be finalized; until then this reader and the gate agree (both ~0). The timezone
    /// conversion has no EF translation, so the authoritative day match runs in memory over a coarse SQL pre-filter (a
    /// two-day floor safely covers a Central day, which starts at most ~30h before <paramref name="now"/>).
    /// </para>
    /// <para>
    /// <b>Realized only.</b> An open position's unrealized P&amp;L is not counted — this is a projection over
    /// <b>closed</b> trades with no venue round-trip (the read is "without composing a send"), matching the
    /// consistency window's realized-only measure. <b>Zero, not absence,</b> when nothing closed today: a quiet day
    /// has full headroom, so the honest answer is <c>0</c> consumed, never null.
    /// </para>
    /// <para>
    /// Owner-scoped by the R-20 query filter (<see cref="Entities.Trade"/> is <c>IUserOwned</c>): a request read, so
    /// it sees only the caller's trades and takes no <c>IgnoreQueryFilters</c>.
    /// </para>
    /// <para>
    /// <b>Mode-scoped (R-14, gh#746).</b> Only trades whose <see cref="Entities.Trade.Mode"/> equals
    /// <paramref name="mode"/> are summed. <c>Trade.Mode</c> is the mode at <b>placement</b> and is never rewritten, so
    /// an account that moved Practice → Live still carries its practice rows here; passing the account's <i>current</i>
    /// mode keeps practice money out of a live limit and vice-versa — the exact blending R-14 exists to prevent,
    /// enforced one layer above the gate. The existing <c>(AccountId, ClosedAt)</c> index still range-seeks the day;
    /// <c>Mode</c> is a cheap residual over the handful of rows in one account's day, so no new index is warranted at
    /// single-operator volume.
    /// </para>
    /// <para>
    /// <b>Never pass <see cref="TradingMode.Undeclared"/> (gh#746 review).</b> <c>Trade.Mode</c> is check-constrained
    /// never to be <c>Undeclared</c>, so filtering by it matches <b>zero rows for any account, permanently</b> — a
    /// silent, always-empty read that would report full headroom on an account that really lost money under a prior
    /// mode. An <c>Undeclared</c> account trades nowhere, so this <b>throws</b> on <c>Undeclared</c> rather than
    /// return that accidental 0; a caller must treat an Undeclared account as inert <i>before</i> calling (the
    /// headroom read 404s, the R-4 throttle reads 0).
    /// </para>
    /// </remarks>
    /// <param name="database">The scoped, R-20-filtered context.</param>
    /// <param name="accountId">The account whose day is summed.</param>
    /// <param name="mode">
    /// The account's <b>current</b> declared mode — the read counts only trades taken under it (R-14). The journal is
    /// historical, so a practice trade on a now-live account must never count toward a live limit; the caller passes
    /// the account's mode <b>now</b>, not any trade's stored mode.
    /// </param>
    /// <param name="now">The current time, supplied by the caller — the trading-day boundary is derived from it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The signed realized P&amp;L for today (positive net profit, negative net loss); <c>0</c> when nothing closed today.</returns>
    public static async Task<decimal> TodayRealizedPnLForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        TradingMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Refuse Undeclared loudly (gh#746 review). Trade.Mode is check-constrained never to be Undeclared, so a
        // Trade.Mode == Undeclared filter matches zero rows for ANY account and would silently read as "no realized
        // P&L" -- reporting full headroom on an account that really lost money. No caller should pass it (an
        // Undeclared account trades nowhere); the callers treat it as inert, and this enforces that contract so a
        // future one that forgets fails fast rather than silently misleading a risk surface.
        GuardMode(mode);

        DateTime today = MarketClock.ToMarketTime(now).Date;
        DateTimeOffset floor = now.AddDays(-2); // coarse bound; the in-memory Central-date match below is authoritative

        List<(DateTimeOffset ClosedAt, decimal RealizedPnL)> recent = await database.Trades
            .Where(trade => trade.AccountId == accountId && trade.Mode == mode
                && trade.ClosedAt != null && trade.ClosedAt >= floor && trade.RealizedPnL != null)
            .Select(trade => new ValueTuple<DateTimeOffset, decimal>(trade.ClosedAt!.Value, trade.RealizedPnL!.Value))
            .ToListAsync(cancellationToken);

        return recent
            .Where(trade => MarketClock.ToMarketTime(trade.ClosedAt).Date == today)
            .Sum(trade => trade.RealizedPnL);
    }

    /// <summary>
    /// Sums the account's realized P&amp;L <b>per Central trading day</b> over <paramref name="from"/>..<paramref name="to"/>
    /// (both inclusive) — the gh#1062 (R-8/R-9) generalization of <see cref="TodayRealizedPnLForAccountAsync"/> from
    /// "today" to an arbitrary range, for the P&amp;L-by-day calendar read. Same rules as the day-scoped reader: the
    /// Central calendar day (not UTC), realized-and-closed trades only, R-20 owner scoping, and R-14 mode scoping
    /// (gh#746) — see that method's remarks for the full rationale, which apply unchanged here.
    /// </summary>
    /// <param name="database">The scoped, R-20-filtered context.</param>
    /// <param name="accountId">The account whose days are summed.</param>
    /// <param name="mode">The account's <b>current</b> declared mode (R-14) — never <see cref="TradingMode.Undeclared"/>.</param>
    /// <param name="from">The first Central trading day to include.</param>
    /// <param name="to">The last Central trading day to include.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// One entry per Central day that closed at least one realized trade, ascending by date — <b>no entry</b> for a
    /// quiet day (the empty list is the honest "nothing happened" for a range, mirroring the single-day reader's
    /// <c>0</c>). The caller decides whether to fill gaps for a calendar display.
    /// </returns>
    public static async Task<IReadOnlyList<DailyRealized>> RealizedPnLByDayForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        TradingMode mode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        GuardMode(mode);

        DateTimeOffset windowStart = CentralDayStartUtc(from);
        DateTimeOffset windowEndExclusive = CentralDayStartUtc(to.AddDays(1));

        List<(DateTimeOffset ClosedAt, decimal RealizedPnL)> rows = await database.Trades
            .Where(trade => trade.AccountId == accountId && trade.Mode == mode
                && trade.ClosedAt != null && trade.ClosedAt >= windowStart && trade.ClosedAt < windowEndExclusive
                && trade.RealizedPnL != null)
            .Select(trade => new ValueTuple<DateTimeOffset, decimal>(trade.ClosedAt!.Value, trade.RealizedPnL!.Value))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .GroupBy(row => DateOnly.FromDateTime(MarketClock.ToMarketTime(row.ClosedAt).Date))
                .Select(group => new DailyRealized(group.Key, group.Sum(row => row.RealizedPnL), group.Count()))
                .OrderBy(day => day.Date),
        ];
    }

    /// <summary>
    /// The account's realized, closed trades on one <b>Central trading day</b> (gh#1062, R-8) — the day-detail
    /// drill-down behind the P&amp;L-by-day calendar. Same rules as <see cref="RealizedPnLByDayForAccountAsync"/>: the
    /// Central calendar day (not UTC), realized-and-closed only, R-20 owner scoping, R-14 mode scoping (gh#746).
    /// </summary>
    /// <param name="database">The scoped, R-20-filtered context.</param>
    /// <param name="accountId">The account whose day is read.</param>
    /// <param name="mode">The account's <b>current</b> declared mode (R-14) — never <see cref="TradingMode.Undeclared"/>.</param>
    /// <param name="day">The Central trading day to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The day's realized trades, oldest first; empty (not an error) on a quiet day.</returns>
    public static async Task<IReadOnlyList<Trade>> TradesForDayForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        TradingMode mode,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        GuardMode(mode);

        DateTimeOffset windowStart = CentralDayStartUtc(day);
        DateTimeOffset windowEndExclusive = CentralDayStartUtc(day.AddDays(1));

        return await database.Trades
            .Where(trade => trade.AccountId == accountId && trade.Mode == mode
                && trade.ClosedAt != null && trade.ClosedAt >= windowStart && trade.ClosedAt < windowEndExclusive
                && trade.RealizedPnL != null)
            .OrderBy(trade => trade.ClosedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses <see cref="TradingMode.Undeclared"/> loudly (gh#746 review), shared by every reader in this family.
    /// See <see cref="TodayRealizedPnLForAccountAsync"/>'s remarks for the full rationale: filtering
    /// <c>Trade.Mode</c> by <c>Undeclared</c> silently matches zero rows for any account (the column is
    /// check-constrained never to hold it), which would misreport as "nothing realized" rather than fail loudly.
    /// </summary>
    private static void GuardMode(TradingMode mode)
    {
        if (mode == TradingMode.Undeclared)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), "Undeclared is not a journalable trading mode; treat an Undeclared account as inert.");
        }
    }

    /// <summary>
    /// UTC instant at which the Central calendar day <paramref name="day"/> begins — <see cref="MarketClock.CentralDayStartUtc"/>
    /// fed a midday-UTC instant on that date so the Central-time conversion cannot roll the date itself (a UTC
    /// midnight or late-evening instant sits close enough to the Central boundary that the conversion could land on
    /// the adjacent calendar date; UTC noon never does, for any US time zone).
    /// </summary>
    private static DateTimeOffset CentralDayStartUtc(DateOnly day) =>
        MarketClock.CentralDayStartUtc(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
}

/// <summary>
/// One Central trading day's realized outcome (gh#1062, R-8/R-9) — a row of the P&amp;L-by-day calendar read.
/// </summary>
/// <param name="Date">The Central trading day.</param>
/// <param name="RealizedPnL">The day's signed realized P&amp;L (positive net profit, negative net loss).</param>
/// <param name="TradeCount">How many realized trades closed that day.</param>
public sealed record DailyRealized(DateOnly Date, decimal RealizedPnL, int TradeCount);
