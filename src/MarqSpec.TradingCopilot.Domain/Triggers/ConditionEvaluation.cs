namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The result of evaluating an indicator condition against a (possibly missing) value (gh#385). It deliberately
/// separates <b>"cannot measure"</b> from <b>"measured and not satisfied"</b> so the debounce never fires — nor
/// re-arms — on the absence of data.
/// </summary>
public enum ConditionEvaluation
{
    /// <summary>
    /// The indicator has no value (null): insufficient history, a projection not caught up, or no bars. Cannot
    /// measure, so it must <b>never fire and never re-arm</b> — the fail-closed contract (gh#311, R-22). The zero
    /// value, so an unset result is the harmless one.
    /// </summary>
    Unmeasurable = 0,

    /// <summary>Measured, and the condition is <b>not</b> satisfied.</summary>
    NotSatisfied = 1,

    /// <summary>Measured, and the condition <b>is</b> satisfied.</summary>
    Satisfied = 2,

    /// <summary>
    /// The condition is structurally unable to produce an answer (reserved for composite / future condition types;
    /// a threshold condition never returns this because its comparison is refused unset at construction). Treated
    /// exactly like <see cref="Unmeasurable"/> by the debounce — hold, never fire.
    /// </summary>
    Indeterminate = 3,
}
