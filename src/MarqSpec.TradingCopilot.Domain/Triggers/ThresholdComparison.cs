namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Which side of the threshold makes an indicator condition true (gh#385, A1). A <b>refusable zero</b>:
/// <see cref="Unknown"/> is the unset value and never satisfies — a comparison must be declared, or a trigger
/// could fire on an unset default. Refused at construction and by a DB check, mirroring
/// <c>ConditionalCrossDirection</c>.
/// </summary>
public enum ThresholdComparison
{
    /// <summary>Unset — never satisfied; refused at construction and by a DB check. Never a real comparison.</summary>
    Unknown = 0,

    /// <summary>Satisfied while the value is <b>at or below</b> the threshold (value ≤ threshold).</summary>
    Below = 1,

    /// <summary>Satisfied while the value is <b>at or above</b> the threshold (value ≥ threshold).</summary>
    Above = 2,
}
