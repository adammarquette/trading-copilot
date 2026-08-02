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

    /// <summary>
    /// The trigger fired and the entry is <b>mid-flight</b>: a <b>durable pre-transmit intent</b> (gh#577) committed
    /// <i>before</i> the venue is touched, so a fault anywhere in the transmit→journal window (a DB fault or a
    /// shutdown after the venue accepted but before the order journaled) leaves the conditional <b>here</b>, not
    /// back at <see cref="Pending"/> where a pure level test would blind-re-fire it. It is a transient at rest: the
    /// firing pass moves it on to <see cref="Fired"/> (accepted + journaled) or back to <see cref="Pending"/> (the
    /// venue never accepted). One found <b>persisting</b> across a restart is an impossible combination the
    /// rehydration pass flags and fails safe on (ADR-0013 — reconcile/surface against venue truth, never blindly
    /// re-fire), never a resting state.
    /// </summary>
    Firing = 5,
}
