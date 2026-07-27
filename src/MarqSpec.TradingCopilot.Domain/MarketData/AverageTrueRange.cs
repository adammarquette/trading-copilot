using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// Average true range over a bar series (gh#310, R-1) — the first indicator, and the one
/// <c>StopProximityMetric.AverageTrueRange</c> has been refused for want of.
/// </summary>
/// <remarks>
/// <para>
/// <b>True</b> range, not the bar's own range: it takes the wider of this bar's high−low and its distance from
/// the <i>previous close</i>, so a gap open counts as the risk it is. That is the whole reason ATR is the metric
/// ADR-0007 names for a promotion band — a stop placed by a measure that cannot see gaps is placed by a measure
/// that ignores the case it exists for.
/// </para>
/// <para>
/// <b>Wilder's smoothing</b>, not a simple moving average of true range. That is what "ATR" means on the charts
/// the operator reads, and a stop distance that disagrees with their chart is worse than no ATR: they would have
/// no way to tell a bug from a definition.
/// </para>
/// <para>
/// A <b>pure function of the bars handed in</b> — no clock, no storage, no state. That is what makes ADR-0001's
/// rebuild story true rather than aspirational: recomputing over the same clean-historical series reproduces the
/// same values exactly, so a rebuild restores history instead of quietly rewriting it.
/// </para>
/// </remarks>
public static class AverageTrueRange
{
    /// <summary>
    /// Computes ATR for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in <b>ascending</b> time order.</param>
    /// <param name="period">How many true ranges the average spans (Wilder's default is 14).</param>
    /// <returns>
    /// One entry per bar. <see langword="null"/> until the period is satisfied — the first bar has no previous
    /// close and therefore no true range, so the first value lands at index <paramref name="period"/> and needs
    /// <c>period + 1</c> bars. <b>No value is deliberate, and better than a partial one</b>: an ATR averaged over
    /// a short window looks entirely ordinary and would place a stop at the wrong distance.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="bars"/> is not in ascending time order.</exception>
    public static IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);

        decimal?[] values = new decimal?[bars.Count];
        if (bars.Count == 0)
        {
            return values;
        }

        // Out of order does not fail loudly on its own -- true range reads the PREVIOUS bar's close, so a
        // shuffled series quietly computes a different, wrong number. Refuse it at the boundary.
        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i].OpenTime <= bars[i - 1].OpenTime)
            {
                throw new ArgumentException(
                    "Bars must be in ascending time order: true range is measured against the previous bar's "
                    + "close, so a mis-ordered series computes a wrong value rather than failing.",
                    nameof(bars));
            }
        }

        if (bars.Count <= period)
        {
            return values; // not enough history for even the seed
        }

        decimal seedTotal = 0m;
        for (int i = 1; i <= period; i++)
        {
            seedTotal += TrueRange(bars[i], bars[i - 1]);
        }

        decimal current = seedTotal / period;
        values[period] = current;

        for (int i = period + 1; i < bars.Count; i++)
        {
            // Wilder: the new observation gets a 1/period weight, the running value keeps the rest.
            current = ((current * (period - 1)) + TrueRange(bars[i], bars[i - 1])) / period;
            values[i] = current;
        }

        return values;
    }

    /// <summary>
    /// This bar's true range: the widest of its own range and its two distances from the previous close — which
    /// is how an overnight or news gap is counted rather than missed.
    /// </summary>
    private static decimal TrueRange(Bar bar, Bar previous)
    {
        decimal previousClose = previous.Close.Value;
        decimal ownRange = bar.High.Value - bar.Low.Value;
        decimal upFromClose = Math.Abs(bar.High.Value - previousClose);
        decimal downFromClose = Math.Abs(bar.Low.Value - previousClose);

        return Math.Max(ownRange, Math.Max(upFromClose, downFromClose));
    }
}
