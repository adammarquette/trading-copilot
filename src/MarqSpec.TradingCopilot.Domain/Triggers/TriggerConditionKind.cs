namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Which kind of condition a trigger evaluates (R-4 / R-7, ADR-0008) — the persisted discriminator that lets a
/// second condition kind land as additive columns rather than a reshape.
/// </summary>
/// <remarks>
/// Only <see cref="IndicatorThreshold"/> is built. Order-flow (R-3), composite, cross-asset and time-of-day
/// conditions become sibling <c>TriggerCondition</c> subtypes behind new kinds when they land. <see cref="Unknown"/>
/// is the refusable zero.
/// </remarks>
public enum TriggerConditionKind
{
    /// <summary>Not a real kind — refused.</summary>
    Unknown = 0,

    /// <summary>A single indicator value compared to a threshold (R-22).</summary>
    IndicatorThreshold = 1,
}
