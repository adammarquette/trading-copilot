namespace MarqSpec.TradingCopilot.Domain.Flatten;

/// <summary>What the auto-flatten should do at a given moment (R-13).</summary>
public enum FlattenAction
{
    /// <summary>Nothing to do — either the deadline is far off, or there is no exposure to close.</summary>
    None,

    /// <summary>The deadline is close and a position is open. Warn the operator; escalate as it nears.</summary>
    Warn,

    /// <summary>Close the position now. The one order action the system takes without per-trade confirmation.</summary>
    Flatten,

    /// <summary>
    /// The deadline and its firing window both passed with exposure still open — the degraded case. Not a
    /// silent no-op, and not a flatten fired hours late into the settlement window: it is escalated (ADR-0013).
    /// </summary>
    Missed,

    /// <summary>
    /// The operator disabled auto-flatten for this market. Reported distinctly from <see cref="None"/> so the
    /// override stays visible and journalable rather than looking like "nothing to do" (R-13).
    /// </summary>
    Disabled,
}
