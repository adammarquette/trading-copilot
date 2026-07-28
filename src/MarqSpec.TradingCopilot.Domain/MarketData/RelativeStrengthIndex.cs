using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.Domain.MarketData;

/// <summary>
/// Relative strength index over a bar series (R-22) — the second indicator, and the one that proves the pipeline
/// is a framework rather than an ATR special case.
/// </summary>
/// <remarks>
/// <para>
/// RSI compares the average <b>gain</b> to the average <b>loss</b> of the close-to-close moves over a period, and
/// maps the ratio onto <c>0..100</c>: <c>100 − 100 / (1 + avgGain/avgLoss)</c>. It reads the <b>close</b> only —
/// unlike ATR, which needs the high, low and the previous close — but it shares ATR's <b>Wilder smoothing</b>, so
/// it matches the "RSI" the operator reads on their chart rather than a simple-moving-average lookalike.
/// </para>
/// <para>
/// The degenerate ratios are pinned so the number is never a divide-by-zero or a silent lie: <b>only gains</b>
/// (no losses) is <c>100</c>, <b>only losses</b> is <c>0</c>, and a <b>flat</b> window with neither is the neutral
/// <c>50</c> rather than a spurious extreme. A <b>pure function of the bars</b> — no clock, no state — so a
/// rebuild reproduces it exactly (ADR-0001).
/// </para>
/// </remarks>
public static class RelativeStrengthIndex
{
    /// <summary>
    /// Computes RSI for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in <b>ascending</b> time order.</param>
    /// <param name="period">How many close-to-close moves the averages span (Wilder's default is 14).</param>
    /// <returns>
    /// One entry per bar in <c>0..100</c>. <see langword="null"/> until the period is satisfied — the first bar
    /// has no previous close and therefore no move, so the first value lands at index <paramref name="period"/>
    /// and needs <c>period + 1</c> bars.
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

        // RSI reads the PREVIOUS close for every move, so a shuffled series does not fail -- it quietly computes a
        // different, wrong number. Refuse it at the boundary, exactly as ATR does.
        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i].OpenTime <= bars[i - 1].OpenTime)
            {
                throw new ArgumentException(
                    "Bars must be in ascending time order: RSI is measured against the previous bar's close, so "
                    + "a mis-ordered series computes a wrong value rather than failing.",
                    nameof(bars));
            }
        }

        if (bars.Count <= period)
        {
            return values; // not enough history for even the seed
        }

        decimal gainTotal = 0m;
        decimal lossTotal = 0m;
        for (int i = 1; i <= period; i++)
        {
            decimal change = bars[i].Close.Value - bars[i - 1].Close.Value;
            if (change > 0m)
            {
                gainTotal += change;
            }
            else
            {
                lossTotal += -change;
            }
        }

        // Seed from the TOTALS, not the averages: RSI = 100·avgGain/(avgGain+avgLoss), and the /period in each
        // average cancels, so 100·gainTotal/(gainTotal+lossTotal) is the same number computed without the
        // non-terminating decimal division (5/3 etc.) that would otherwise drift the seed off an exact value.
        values[period] = Rsi(gainTotal, lossTotal);

        decimal averageGain = gainTotal / period;
        decimal averageLoss = lossTotal / period;
        for (int i = period + 1; i < bars.Count; i++)
        {
            decimal change = bars[i].Close.Value - bars[i - 1].Close.Value;
            decimal gain = change > 0m ? change : 0m;
            decimal loss = change < 0m ? -change : 0m;

            // Wilder: the new move gets a 1/period weight, the running average keeps the rest.
            averageGain = ((averageGain * (period - 1)) + gain) / period;
            averageLoss = ((averageLoss * (period - 1)) + loss) / period;
            values[i] = Rsi(averageGain, averageLoss);
        }

        return values;
    }

    /// <summary>
    /// Maps a gain / loss pair onto the <c>0..100</c> RSI scale, pinning the degenerate ratios. Uses the stable
    /// <c>100·gain/(gain+loss)</c> form — algebraically identical to <c>100 − 100/(1 + gain/loss)</c> but one
    /// division rather than two, so it carries less rounding.
    /// </summary>
    private static decimal Rsi(decimal gain, decimal loss)
    {
        if (gain == 0m && loss == 0m)
        {
            return 50m; // a flat window has no directional pressure -- neutral, not a spurious 0 or 100
        }

        if (loss == 0m)
        {
            return 100m; // only gains -- short-circuit rather than divide by zero
        }

        return 100m * gain / (gain + loss); // gain == 0 -> 0
    }
}
