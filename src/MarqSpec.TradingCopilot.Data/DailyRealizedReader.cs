using MarqSpec.TradingCopilot.Domain.Flatten;
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
    /// Sums the account's realized P&amp;L for the <b>CME/Central trading day</b> containing <paramref name="now"/>.
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
    /// </remarks>
    /// <param name="database">The scoped, R-20-filtered context.</param>
    /// <param name="accountId">The account whose day is summed.</param>
    /// <param name="now">The current time, supplied by the caller — the trading-day boundary is derived from it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The signed realized P&amp;L for today (positive net profit, negative net loss); <c>0</c> when nothing closed today.</returns>
    public static async Task<decimal> TodayRealizedPnLForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        DateTime today = MarketClock.ToMarketTime(now).Date;
        DateTimeOffset floor = now.AddDays(-2); // coarse bound; the in-memory Central-date match below is authoritative

        List<(DateTimeOffset ClosedAt, decimal RealizedPnL)> recent = await database.Trades
            .Where(trade => trade.AccountId == accountId
                && trade.ClosedAt != null && trade.ClosedAt >= floor && trade.RealizedPnL != null)
            .Select(trade => new ValueTuple<DateTimeOffset, decimal>(trade.ClosedAt!.Value, trade.RealizedPnL!.Value))
            .ToListAsync(cancellationToken);

        return recent
            .Where(trade => MarketClock.ToMarketTime(trade.ClosedAt).Date == today)
            .Sum(trade => trade.RealizedPnL);
    }
}
