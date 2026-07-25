namespace MarqSpec.TradingCopilot.Domain.Audit;

/// <summary>
/// What an <c>AuditRecord</c> attests to (data dictionary §12, engineering §9, ADR-0007).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an uninitialised action
/// must never masquerade as a real audited event. This records the <see cref="ConnectionLoss"/> event (gh#220) and
/// the <see cref="PositionExit"/> retirement (gh#183); the remaining order / guardrail / kill / flatten actions the
/// audit will carry are named in the data dictionary and land as those write sites are wired.
/// </remarks>
public enum AuditAction
{
    /// <summary>Not an action — the refusable zero. Never persisted.</summary>
    Unknown = 0,

    /// <summary>
    /// A venue-connection loss and its recovery (ADR-0013, gh#209/gh#220): the orphaning of a hidden working
    /// stop, its re-arm on reconnect, or its retirement when the position closed during the outage. Each such
    /// transition on a synthetic stop carries an <c>AuditRecord</c> flagged <c>synthetic_risk</c>.
    /// </summary>
    ConnectionLoss = 1,

    /// <summary>
    /// A position exit (ADR-0007, gh#183): the deliberate retirement of a stop plan when its position went flat,
    /// so a reader can tell protection was retired on exit rather than lost. Recorded when OCO-cancel-on-exit sets
    /// a plan to <c>Retired</c>; a flat-position cleanup, so its record does <b>not</b> carry <c>synthetic_risk</c>.
    /// </summary>
    PositionExit = 2,
}
