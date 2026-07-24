namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// How close price must come to the actual stop before it is promoted from hidden to native (ADR-0007).
/// </summary>
/// <remarks>
/// The ADR is explicit that the band is expressed in <b>ticks</b>, <b>ATR</b>, or a <b>fraction of the
/// entry→stop distance</b> — <i>never</i> a percentage of raw price, which would scale with the instrument's
/// absolute level rather than with the risk actually taken.
/// </remarks>
public sealed record StopProximity
{
    private StopProximity(StopProximityMetric metric, decimal value)
    {
        Metric = metric;
        Value = value;
    }

    /// <summary>How the band is measured.</summary>
    public StopProximityMetric Metric { get; }

    /// <summary>The band's magnitude, in the units <see cref="Metric"/> implies.</summary>
    public decimal Value { get; }

    /// <summary>A band of whole ticks.</summary>
    /// <param name="ticks">Tick count; must be positive.</param>
    /// <returns>The proximity band.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticks"/> is not positive.</exception>
    public static StopProximity Ticks(int ticks)
    {
        return ticks <= 0
            ? throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "A proximity band must be positive.")
            : new StopProximity(StopProximityMetric.Ticks, ticks);
    }

    /// <summary>A band expressed as a fraction of the entry→actual-stop distance.</summary>
    /// <param name="fraction">The fraction; must be in (0, 1].</param>
    /// <returns>The proximity band.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is outside (0, 1].</exception>
    public static StopProximity DistanceFraction(decimal fraction)
    {
        return fraction is <= 0m or > 1m
            ? throw new ArgumentOutOfRangeException(
                nameof(fraction), fraction, "A distance fraction must be in (0, 1] — more than the whole distance is not a band.")
            : new StopProximity(StopProximityMetric.DistanceFraction, fraction);
    }

    /// <summary>
    /// A band of average-true-range multiples. <b>Constructable but not yet usable</b> — the indicator pipeline
    /// (R-3) does not exist, so <see cref="StopPlan.Create"/> refuses it rather than silently mis-measuring.
    /// </summary>
    /// <param name="multiple">The ATR multiple; must be positive.</param>
    /// <returns>The proximity band.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiple"/> is not positive.</exception>
    public static StopProximity AverageTrueRange(decimal multiple)
    {
        return multiple <= 0m
            ? throw new ArgumentOutOfRangeException(nameof(multiple), multiple, "An ATR multiple must be positive.")
            : new StopProximity(StopProximityMetric.AverageTrueRange, multiple);
    }
}
