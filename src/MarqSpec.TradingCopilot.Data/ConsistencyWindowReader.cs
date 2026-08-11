using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Reads the profit picture a consistency target is measured against (gh#380) — counting only trades taken under the
/// account's current mode (R-14, gh#746), so a practice day never enters a live account's payout window.
/// </summary>
public static class ConsistencyWindowReader
{
    /// <summary>
    /// Builds the <see cref="ConsistencyWindow"/> for <paramref name="accountId"/> from its closed trades.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Days are US Central, not UTC.</b> A CME session runs past UTC midnight, so grouping on the UTC date
    /// splits one trading day in two — which halves the day's apparent profit and lets an outsized day slip
    /// under the target. <see cref="MarketClock.CentralTime"/> is the same convention the auto-flatten already
    /// uses, so "day" means the same thing across the system.
    /// </para>
    /// <para>
    /// Grouped in memory rather than in SQL because the timezone conversion has no EF translation. The set is
    /// one operator's closed trades for one account, which is small; if that ever stops being true this wants a
    /// persisted daily rollup rather than a cleverer query.
    /// </para>
    /// <para>
    /// Only <b>closed</b> trades count. An open position's profit is not realized, and a consistency target is
    /// measured on realized profit — counting unrealized would make the fraction move with the market rather
    /// than with what actually happened.
    /// </para>
    /// <para>
    /// <b>Never pass <see cref="TradingMode.Undeclared"/> (gh#746 review).</b> <c>Trade.Mode</c> is check-constrained
    /// never to be <c>Undeclared</c>, so filtering by it always matches zero rows and would return
    /// <see cref="ConsistencyWindow.Empty"/> by accident rather than by fact — so this <b>throws</b> on
    /// <c>Undeclared</c>. An <c>Undeclared</c> account trades nowhere; the send path's read
    /// (<c>OrderEndpoints.ComposeAsync</c>) guards Undeclared to an empty window before this, since that send is
    /// refused for being undeclared anyway, so no order is mis-sent.
    /// </para>
    /// </remarks>
    /// <param name="database">The context.</param>
    /// <param name="accountId">The account whose evaluation window is read.</param>
    /// <param name="mode">
    /// The account's <b>current</b> declared mode — only trades taken under it count (R-14, gh#746). The consistency
    /// rule is a per-mode measure, so a practice day on a now-live account must never enter a live payout's window; the
    /// caller passes the account's mode <b>now</b>, not any trade's stored mode. <c>Trade.Mode</c> is the mode at
    /// placement and never rewritten, so the historical journal retains both sides of a mode change.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The window; <see cref="ConsistencyWindow.Empty"/> when the account has closed nothing in that mode.</returns>
    public static async Task<ConsistencyWindow> ConsistencyWindowForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        TradingMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Refuse Undeclared loudly (gh#746 review). Trade.Mode is check-constrained never to be Undeclared, so a
        // Trade.Mode == Undeclared filter always matches zero rows and would return Empty by accident rather than by
        // fact. No caller should pass it; the send-path caller (OrderEndpoints.ComposeAsync) guards it to Empty before
        // this, since an undeclared send is refused anyway.
        if (mode == TradingMode.Undeclared)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), "Undeclared is not a journalable trading mode; treat an Undeclared account as inert.");
        }

        List<(DateTimeOffset ClosedAt, decimal RealizedPnL)> closed = await database.Trades
            .Where(trade => trade.AccountId == accountId && trade.Mode == mode
                && trade.ClosedAt != null && trade.RealizedPnL != null)
            .Select(trade => new ValueTuple<DateTimeOffset, decimal>(trade.ClosedAt!.Value, trade.RealizedPnL!.Value))
            .ToListAsync(cancellationToken);

        if (closed.Count == 0)
        {
            return ConsistencyWindow.Empty;
        }

        List<decimal> perDay = closed
            .GroupBy(trade => MarketClock.ToMarketTime(trade.ClosedAt).Date)
            .Select(day => day.Sum(trade => trade.RealizedPnL))
            .ToList();

        return new ConsistencyWindow(perDay.Sum(), perDay.Max());
    }
}
