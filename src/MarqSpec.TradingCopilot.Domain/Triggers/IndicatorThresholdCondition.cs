namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// A deterministic indicator-threshold condition (gh#385, A1) — for example "RSI(14) on 1m is at or below 30".
/// Pure domain: it reads no clock and no database, it only <see cref="Evaluate"/>s a supplied value. Level and
/// cross are <b>unified</b> — "crosses below 30" is the arming <i>edge</i> of "is below 30", produced by the
/// debounce, not a separate operator here (ADR-0008: the deterministic scan, the LLM never in it).
/// </summary>
/// <remarks>
/// The comparison is a <b>refusable zero</b> (<see cref="ThresholdComparison.Unknown"/>): refused at construction
/// here and by a DB check on the persisted trigger, so a condition can never silently satisfy on an unset default.
/// The indicator is a stable name string (<c>"atr"</c>, <c>"rsi"</c>), matching the indicator projection's own
/// identity — never an enum.
/// </remarks>
public sealed record IndicatorThresholdCondition
{
    /// <summary>Creates a validated condition.</summary>
    /// <param name="instrument">The instrument the indicator is measured on.</param>
    /// <param name="indicator">The indicator name (e.g. <c>atr</c>, <c>rsi</c>) — the projection's own identity.</param>
    /// <param name="period">The indicator period (e.g. Wilder 14). Part of the value's identity.</param>
    /// <param name="resolutionMinutes">The bar size the indicator is measured on. Part of identity.</param>
    /// <param name="comparison">Which side of the threshold satisfies. Must not be <see cref="ThresholdComparison.Unknown"/>.</param>
    /// <param name="threshold">The threshold value.</param>
    /// <exception cref="ArgumentException"><paramref name="indicator"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> or <paramref name="resolutionMinutes"/> is not positive, or <paramref name="comparison"/> is <see cref="ThresholdComparison.Unknown"/>.</exception>
    public IndicatorThresholdCondition(
        InstrumentId instrument,
        string indicator,
        int period,
        int resolutionMinutes,
        ThresholdComparison comparison,
        decimal threshold)
    {
        if (string.IsNullOrWhiteSpace(indicator))
        {
            throw new ArgumentException("An indicator name is required.", nameof(indicator));
        }

        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must be positive.");
        }

        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes), resolutionMinutes, "The resolution must be positive.");
        }

        if (comparison is not (ThresholdComparison.Below or ThresholdComparison.Above))
        {
            // Allowlist the two real comparisons: this refuses the unset zero AND any out-of-range integer a raw
            // client could bind past the enum, so a condition can never satisfy on an undefined comparison.
            throw new ArgumentOutOfRangeException(
                nameof(comparison), comparison, "A threshold comparison must be Below or Above.");
        }

        Instrument = instrument;
        Indicator = indicator.Trim().ToLowerInvariant();
        Period = period;
        ResolutionMinutes = resolutionMinutes;
        Comparison = comparison;
        Threshold = threshold;
    }

    /// <summary>The instrument the indicator is measured on.</summary>
    public InstrumentId Instrument { get; }

    /// <summary>The indicator name — normalized lower-case, matching the projection's identity.</summary>
    public string Indicator { get; }

    /// <summary>The indicator period.</summary>
    public int Period { get; }

    /// <summary>The bar size, in minutes, the indicator is measured on.</summary>
    public int ResolutionMinutes { get; }

    /// <summary>Which side of the threshold satisfies.</summary>
    public ThresholdComparison Comparison { get; }

    /// <summary>The threshold value.</summary>
    public decimal Threshold { get; }

    /// <summary>
    /// Evaluates the condition against an indicator value. A <see langword="null"/> value is
    /// <see cref="ConditionEvaluation.Unmeasurable"/> — never coerced to a default and never fired on
    /// (fail-closed, gh#311).
    /// </summary>
    /// <param name="value">The latest indicator value, or <see langword="null"/> if none can be measured.</param>
    /// <returns>The evaluation.</returns>
    public ConditionEvaluation Evaluate(decimal? value)
    {
        if (value is not decimal measured)
        {
            return ConditionEvaluation.Unmeasurable;
        }

        bool satisfied = Comparison switch
        {
            ThresholdComparison.Below => measured <= Threshold,
            ThresholdComparison.Above => measured >= Threshold,
            _ => false,
        };

        return satisfied ? ConditionEvaluation.Satisfied : ConditionEvaluation.NotSatisfied;
    }
}
