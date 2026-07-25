namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>The lifecycle of a pending conditional order (ADR-0007, gh#176).</summary>
public enum ConditionalStatus
{
    /// <summary>Unset — refused by a DB check; never a real state.</summary>
    Unknown = 0,

    /// <summary>Held local, waiting for its trigger. Not shown at the broker until it fires.</summary>
    Pending = 1,

    /// <summary>The trigger fired and the entry was transmitted through the gate.</summary>
    Fired = 2,

    /// <summary>Cancelled before firing — an adverse drift past the cancel band, or the operator.</summary>
    Cancelled = 3,

    /// <summary>Its validity window passed before the trigger fired.</summary>
    Expired = 4,
}
