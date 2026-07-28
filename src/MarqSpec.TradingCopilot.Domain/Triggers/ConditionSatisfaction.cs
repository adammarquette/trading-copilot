namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// The result of reading a trigger condition against a resolved indicator value (R-4 / R-7, ADR-0008).
/// </summary>
/// <remarks>
/// Four states, kept distinct on purpose. <see cref="Unmeasurable"/> (a <see langword="null"/> indicator — no
/// value can be measured) is <b>not</b> the same as <see cref="NotSatisfied"/>: treating "cannot measure" as
/// "false" would let a data gap re-arm a fired trigger and then spuriously re-fire. <see cref="Indeterminate"/>
/// (inside the hysteresis dead-band) is likewise held apart from both, so a value hovering at the threshold neither
/// fires nor re-arms.
/// </remarks>
public enum ConditionSatisfaction
{
    /// <summary>No value could be measured (the indicator read was <see langword="null"/>). Fail-closed: never acts.</summary>
    Unmeasurable = 0,

    /// <summary>The value is measured and clearly does not meet the condition (past the re-arm boundary).</summary>
    NotSatisfied = 1,

    /// <summary>The value is measured and meets the condition (in the fire zone).</summary>
    Satisfied = 2,

    /// <summary>The value is inside the hysteresis dead-band between fire and re-arm — hold, neither fire nor re-arm.</summary>
    Indeterminate = 3,
}
