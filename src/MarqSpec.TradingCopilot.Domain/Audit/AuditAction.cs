namespace MarqSpec.TradingCopilot.Domain.Audit;

/// <summary>
/// What an <c>AuditRecord</c> attests to (data dictionary §12, engineering §9, ADR-0007).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the refusable zero (the fail-closed-zero convention, gh#60): an uninitialised action
/// must never masquerade as a real audited event. This records the <see cref="ConnectionLoss"/> event (gh#220), the
/// <see cref="PositionExit"/> retirement (gh#183), an operator <see cref="OrderCancelled"/> (gh#250), an
/// operator <see cref="OrderModified"/> reprice (gh#259), the kill switch's <see cref="KillSwitchEngaged"/> /
/// <see cref="KillSwitchDisengaged"/> transitions and the <see cref="AutoFlatten"/> run (gh#765) — the two
/// safety-critical write sites §9 named as deferred until they were wired.
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

    /// <summary>
    /// A working order left the book without ever holding a position (ADR-0007, gh#250): an <b>operator</b> cancel
    /// via the order API, or a <b>venue-detected</b> cancel / rejection reconciled by the account-event stream
    /// (gh#219). Its now-orphaned stop plan is retired with it (else the promotion watcher could promote a native
    /// stop for an order that never filled). Recorded so a reader can tell the order left deliberately, not lost; a
    /// never-filled entry, so its record does <b>not</b> carry <c>synthetic_risk</c> (no live position rested on it).
    /// </summary>
    OrderCancelled = 3,

    /// <summary>
    /// An operator repriced a <b>working</b> order in place via the order API (ADR-0007, gh#259): the resting
    /// entry's price (and its hidden working stop) moved, re-gated at the <b>unchanged</b> size, without a
    /// cancel/replace. The <c>Before</c>/<c>After</c> carry the entry-price transition. A never-filled resting
    /// entry, so — like <see cref="OrderCancelled"/> — its record does <b>not</b> carry <c>synthetic_risk</c>: no
    /// live position rested on the reprice, and the always-native safety stop is untouched.
    /// </summary>
    OrderModified = 4,

    /// <summary>
    /// The kill switch was <b>engaged</b> (ADR-0007, R-11, gh#189/gh#765): outbound orders disabled, working orders
    /// cancelled, and — per the mode — open positions flattened or halted. The <c>Source</c> records what tripped it
    /// (operator, a guardrail, or the dead-man's switch); <c>Before</c>/<c>After</c> carry the engaged transition and
    /// <c>Detail</c> the mode, counts, and reason. An account/system-level action, so its <c>Placement</c> is
    /// <see cref="AuditPlacement.None"/> and it carries no <c>synthetic_risk</c>.
    /// </summary>
    KillSwitchEngaged = 5,

    /// <summary>
    /// The kill switch was <b>disengaged</b> (gh#189/gh#765): outbound orders re-enabled. Recorded so the history
    /// survives — a re-engage no longer overwrites the single <c>KillSwitchState</c> row and loses the prior
    /// transition. Its <c>Placement</c> is <see cref="AuditPlacement.None"/>.
    /// </summary>
    KillSwitchDisengaged = 6,

    /// <summary>
    /// The auto-flatten fired against a position at its deadline (R-13, ADR-0013, gh#185/gh#765): the safety-critical
    /// close before the CME session ends. <c>After</c> carries the outcome (flat, or escalated with exposure still
    /// open) and <c>Detail</c> what it closed; <c>Source</c> is <see cref="AuditSource.Scheduler"/>. Its
    /// <c>Placement</c> is <see cref="AuditPlacement.None"/> — it acts on the position, not a single protective leg.
    /// </summary>
    AutoFlatten = 7,

    /// <summary>
    /// A journal <c>Outcome</c> was <b>hard-deleted</b> (R-15, gh#909): the explicit, confirmed removal whose content
    /// is gone, but whose <b>fact of deletion is still recorded</b> — <c>Detail</c> names the removed outcome and its
    /// resolution so the removal survives in the trail even though the row does not. A data-removal audit, not a
    /// safety action: its <c>Placement</c> is <see cref="AuditPlacement.None"/> (it concerns no protective leg), it
    /// carries no <c>synthetic_risk</c>, and — being outside the kill / flatten set — its <c>Source</c> is
    /// <see langword="null"/> (<c>CK_AuditRecords_Source_MatchesAction</c>).
    /// </summary>
    OutcomeHardDeleted = 8,
}
