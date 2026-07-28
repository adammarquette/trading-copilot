namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// How an indicator value is compared to a threshold in a trigger condition (R-4 / R-7, ADR-0008).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (a defaulted / corrupt value never satisfies), mirroring the
/// <c>ConditionalCrossDirection</c> convention. There is deliberately no <c>CrossesBelow</c> / <c>CrossesAbove</c>:
/// a cross is the <i>arming edge</i> of a level (see <see cref="TriggerDebounce"/>), not a separate operator, so
/// "RSI crosses below 30" and "RSI is below 30" are the same <see cref="Below"/> condition.
/// </remarks>
public enum IndicatorComparison
{
    /// <summary>Not a real comparison — never satisfies (fail-closed).</summary>
    Unknown = 0,

    /// <summary>The value is at or below the threshold.</summary>
    Below = 1,

    /// <summary>The value is at or above the threshold.</summary>
    Above = 2,
}
