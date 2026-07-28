using MarqSpec.TradingCopilot.Domain.Flatten;
using MarqSpec.TradingCopilot.Domain.Risk;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data;

/// <summary>
/// Reads the profit picture a consistency target is measured against (gh#380).
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
    /// </remarks>
    /// <param name="database">The context.</param>
    /// <param name="accountId">The account whose evaluation window is read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The window; <see cref="ConsistencyWindow.Empty"/> when the account has closed nothing.</returns>
    public static async Task<ConsistencyWindow> ConsistencyWindowForAccountAsync(
        this TradingCopilotDbContext database,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        List<(DateTimeOffset ClosedAt, decimal RealizedPnL)> closed = await database.Trades
            .Where(trade => trade.AccountId == accountId && trade.ClosedAt != null && trade.RealizedPnL != null)
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
