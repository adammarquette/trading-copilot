namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// A trigger's machine-evaluable condition (R-4 / R-7, ADR-0008) — a pure predicate the deterministic scan reads.
/// </summary>
/// <remarks>
/// Polymorphic on purpose: <see cref="IndicatorThresholdCondition"/> is the only kind built, but order-flow (R-3),
/// composite, cross-asset and time-of-day conditions land later as sibling subtypes the evaluator switches on — no
/// rewrite. Like <c>ConditionalTrigger</c> / <c>StopPlan.ShouldPromote</c>, it is a pure function of what is stored:
/// the caller resolves the inputs (reads the indicator) and passes the value in; the condition never touches I/O or
/// a clock.
/// </remarks>
public abstract record TriggerCondition
{
    /// <summary>
    /// Reads the condition against a resolved value.
    /// </summary>
    /// <param name="indicatorValue">The resolved indicator value, or <see langword="null"/> when none could be measured.</param>
    /// <returns>Whether the condition is satisfied, not, unmeasurable, or inside the hysteresis dead-band.</returns>
    public abstract ConditionSatisfaction Evaluate(decimal? indicatorValue);
}
