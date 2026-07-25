namespace MarqSpec.TradingCopilot.Domain.Execution;

/// <summary>
/// How an order entered the journal — the path the operator took to place it (R-11, ADR-0007). Every path runs
/// the <b>same</b> enforcing gate; this records <b>which</b> one, so a reader can tell a reviewed take from a
/// one-action send, and an as-is take from a deviation (R-11 records deviations).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): it is never written, and
/// a DB check refuses it. The column is nullable only to admit rows journaled before this field existed — a new
/// order always records a real method.
/// </remarks>
public enum OrderEntryMethod
{
    /// <summary>Not a method — the refusable zero. Never written.</summary>
    Unknown = 0,

    /// <summary>A manual ticket the operator authored and sent directly — no suggestion, no staging step.</summary>
    Manual = 1,

    /// <summary>A suggestion <b>Approved &amp; armed</b> for review, then taken <b>unchanged</b>.</summary>
    ArmedTake = 2,

    /// <summary>An armed ticket the operator <b>edited</b> before taking — a deviation from the proposal (R-11).</summary>
    ModifiedTake = 3,

    /// <summary>
    /// A suggestion sent in <b>one action</b>, skipping the manual review (the opt-in fast path, gh#181). Skips the
    /// review, never the gate.
    /// </summary>
    SendAsIs = 4,

    /// <summary>Placed by the <b>on-trigger</b> conditional watcher when its condition met (ADR-0007, gh#176/gh#198).</summary>
    Conditional = 5,
}
