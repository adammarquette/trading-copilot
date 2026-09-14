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
    /// A band of average-true-range multiples. Measured by the indicator projection (gh#310) and resolved by the
    /// caller through <see cref="ResolveDistance"/>.
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

    /// <summary>
    /// Turns this band into an <b>absolute price distance</b> — or <see langword="null"/> when it cannot be
    /// measured (gh#311).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seam the delivery decision (gh#13, 2026-07-26) put in the <b>caller</b>: the promotion watcher
    /// fetches whatever an ATR band needs and calls this, then hands the resulting distance to
    /// <see cref="StopPlan.ShouldPromote"/>. So the metric is interpreted in exactly one place, the plan itself
    /// stays a pure comparison, and a future metric is a new arm here rather than a change to a safety-critical
    /// value object.
    /// </para>
    /// <para>
    /// <b>Null is "cannot measure", and the caller must not promote on it.</b> Every arm that cannot produce a
    /// defensible number returns null rather than a fallback: a substituted distance would place the stop at a
    /// distance nobody chose, which is the silent mis-measurement this band's original refusal existed to
    /// prevent. Note that null is <i>not</i> the same as zero — zero is a real band meaning "promote once price
    /// touches the stop", so collapsing an unmeasurable band to it would promote, not abstain.
    /// </para>
    /// </remarks>
    /// <param name="instrument">The instrument, for its tick size.</param>
    /// <param name="entry">The entry price — one end of the risk distance a fractional band measures.</param>
    /// <param name="actualStop">The working stop — the other end.</param>
    /// <param name="averageTrueRange">
    /// The current ATR, when one could be read. Ignored by every metric that does not use it, so a caller may
    /// pass what it has without first knowing which metric this is.
    /// </param>
    /// <returns>The distance in the instrument's price units, or <see langword="null"/> when unmeasurable.</returns>
    public decimal? ResolveDistance(
        InstrumentSpec instrument,
        Price entry,
        Price actualStop,
        decimal? averageTrueRange)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        return Metric switch
        {
            StopProximityMetric.Ticks => Value * instrument.TickSize,
            StopProximityMetric.DistanceFraction => Value * Math.Abs(entry.Value - actualStop.Value),

            // A non-positive ATR is not a narrow band, it is an absent measurement: Wilder's smoothing cannot
            // produce one from real bars, so zero or below means no history, a stalled projection, or corruption.
            StopProximityMetric.AverageTrueRange => averageTrueRange is > 0m ? Value * averageTrueRange : null,

            // Unreachable -- the factories above are the only way to build one -- but an unrecognized metric must
            // fail CLOSED. Returning zero here would promote at the stop rather than abstain, and that is the
            // difference between a band nobody chose and no promotion at all.
            _ => null,
        };
    }
}
