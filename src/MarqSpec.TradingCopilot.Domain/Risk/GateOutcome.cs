namespace MarqSpec.TradingCopilot.Domain.Risk;

/// <summary>What the gate decided about an order.</summary>
public enum GateOutcome
{
    /// <summary>The order passes at the requested size.</summary>
    Allowed,

    /// <summary>The order passes, but smaller — a layer bound it.</summary>
    Resized,

    /// <summary>The order does not go. Approved quantity is zero — the gate's "no trade".</summary>
    Blocked,
}
