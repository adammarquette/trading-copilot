using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Audit;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One immutable audit entry (data dictionary §12, engineering §9, ADR-0007): a safety-relevant transition,
/// recorded so the exposure is reconstructable from the table alone. This increment writes the connection-loss
/// lifecycle of a synthetic stop — orphaned on a drop, re-armed or retired on reconnect (gh#209/gh#220) — each
/// row flagged <see cref="SyntheticRisk"/> when a live position was resting on platform-held protection.
/// </summary>
/// <remarks>
/// <b>Append-only.</b> Rows are written once and never updated or deleted — the audit is the durable record of
/// what happened, not a mutable projection of current state. <see cref="StopPlanId"/> is therefore a <b>soft</b>
/// reference (no FK): it preserves the affected stop's id as a permanent historical fact even after that stop row
/// is gone, so the trail stays reconstructable from the table alone — the opposite of the <c>GateDecisionRecord</c>
/// set-null link, which tolerates losing its order. <b>Operator-owned (R-20):</b> a row is stamped with the
/// affected stop's owner and is invisible to any other operator, exactly like the stop it concerns.
/// </remarks>
public class AuditRecord : IUserOwned
{
    /// <summary>The audit entry's unique id.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning operator (R-20) — the affected stop's owner. Rows are visible only to their owner.</summary>
    public Guid UserId { get; set; }

    /// <summary>What happened. <c>required</c>: the zero value (<c>Unknown</c>) is refused by a DB check.</summary>
    public required AuditAction Action { get; set; }

    /// <summary>Where the protection rested. <c>required</c>: <c>Unknown</c> is refused by a DB check.</summary>
    public required AuditPlacement Placement { get; set; }

    /// <summary>
    /// True when a live position was resting on platform-held (synthetic) protection at the moment of the event —
    /// the orphan-risk flag (ADR-0007). The synthetic-risk exposure is exactly the set of rows where this is set.
    /// </summary>
    public bool SyntheticRisk { get; set; }

    /// <summary>The stop plan this entry concerns, when it concerns one — which stop was affected. Soft reference.</summary>
    public Guid? StopPlanId { get; set; }

    /// <summary>The state before the transition (e.g. <c>Hidden</c>). Null when there is no prior state.</summary>
    public string? Before { get; set; }

    /// <summary>The state after the transition (e.g. <c>Orphaned</c>). Null when there is no resulting state.</summary>
    public string? After { get; set; }

    /// <summary>A human-readable description of the event — always populated (engineering §9).</summary>
    public required string Detail { get; set; }

    /// <summary>When the event occurred.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
