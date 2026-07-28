namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>
/// The profit picture a consistency target is measured against (gh#380) — an evaluation window's cumulative
/// profit and its single best day.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best day, not today.</b> The rule a prop firm writes is "no single day may exceed N% of total profit", and
/// the persisted field is documented as <i>best day ÷ total net profit</i>
/// (<c>RiskProfileRecord.MaxBestDayFraction</c>). Measuring today's share instead would miss an evaluation that
/// was already disqualified by an outsized day last week, and would clear itself the moment a new day started.
/// <paramref name="BestDayProfit"/> therefore includes today — today is a candidate for best day, not something
/// compared against the days before it.
/// </para>
/// <para>
/// <b><paramref name="CumulativeProfit"/> includes today too.</b> The total the rule means is the one that will
/// exist at payout.
/// </para>
/// <para>
/// A value object on purpose: the gate is pure and does no I/O, so the caller reads the per-day realized series
/// (from <c>Trade.RealizedPnL</c> over <c>ClosedAt</c>, the only place realized profit is recorded with a time)
/// and hands the gate the two numbers the rule needs.
/// </para>
/// </remarks>
/// <param name="CumulativeProfit">Realized profit across the evaluation window, today included.</param>
/// <param name="BestDayProfit">The largest single-day realized profit in that window, today included.</param>
public sealed record ConsistencyWindow(decimal CumulativeProfit, decimal BestDayProfit)
{
    /// <summary>A window with nothing in it — the rule cannot bind.</summary>
    public static ConsistencyWindow Empty { get; } = new(0m, 0m);

    /// <summary>
    /// The share of cumulative profit contributed by the best day, as a fraction in <c>[0, 1+]</c>.
    /// </summary>
    /// <remarks>
    /// Zero when there is no profit to apportion. That single guard covers three cases that would otherwise each
    /// be a bug: a divide by zero on a fresh account, and the two ways a *losing* window makes the ratio
    /// meaningless — a negative denominator flips the comparison so a good day looks like a breach, and a
    /// negative numerator would report a window whose best day still lost money as consuming the target. A
    /// window that is not in profit cannot disqualify a payout, because there is no payout.
    /// </remarks>
    public decimal ConsumedFraction =>
        CumulativeProfit <= 0m ? 0m : Math.Max(0m, BestDayProfit) / CumulativeProfit;

    /// <summary>Whether the best day's share exceeds <paramref name="targetFraction"/>.</summary>
    /// <param name="targetFraction">The cap, as a fraction (0.4m = "no day may be more than 40% of the total").</param>
    /// <returns><see langword="true"/> when the target is breached.</returns>
    public bool Breaches(decimal targetFraction) => ConsumedFraction > targetFraction;
}
