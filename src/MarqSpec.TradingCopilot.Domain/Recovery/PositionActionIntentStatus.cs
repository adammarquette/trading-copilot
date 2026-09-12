namespace MarqSpec.TradingCopilot.Domain.Recovery;

/// <summary>
/// Lifecycle of a durable pre-transmit intent for an operator position close (gh#1161).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (gh#60). <see cref="Open"/> is transient at runtime — one request
/// resolves it after the attempt. Found persisting across a restart, the close may be live at the venue with no
/// outcome journal behind it: the reconcile sweep (gh#722) surfaces it, and rehydration flags it
/// (<see cref="DecisionInconsistencyKind.PositionActionMidIntent"/>). Never silently aged out.
/// </remarks>
public enum PositionActionIntentStatus
{
    /// <summary>Not a status — the fail-closed zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>Committed before transmit; the attempt has not yet written its outcome.</summary>
    Open = 1,

    /// <summary>The post-attempt journal ran and stamped the verified outcome on this row.</summary>
    Resolved = 2,
}
