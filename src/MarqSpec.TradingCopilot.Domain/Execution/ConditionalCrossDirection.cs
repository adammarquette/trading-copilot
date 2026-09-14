namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// Which way price must cross the trigger for a conditional order to fire (ADR-0007, gh#176). A <b>refusable
/// zero</b>: <see cref="Unknown"/> is the unset value and never fires — a direction must be declared, or the
/// order would rest forever.
/// </summary>
public enum ConditionalCrossDirection
{
    /// <summary>Unset — never fires, and refused at construction and by a DB check. Never a real direction.</summary>
    Unknown = 0,

    /// <summary>Fires when price <b>rises to</b> the trigger (price ≥ trigger) — e.g. a breakout entry.</summary>
    RisesTo = 1,

    /// <summary>Fires when price <b>falls to</b> the trigger (price ≤ trigger) — e.g. a pullback entry.</summary>
    FallsTo = 2,
}
