namespace MarqSpec.TradingCopilot.Domain.Triggers;

/// <summary>
/// Whether the operator has confirmed a standing trigger for live evaluation (gh#470, R-7, ADR-0008) — the
/// authorship-to-armed gate that makes "plain rules → <b>confirmed</b> deterministic triggers" (gh#15) true of the
/// <i>rule</i>, not only of the firing.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> <c>Enabled</c>. <c>Enabled</c> is the live/paused switch an operator flips on a trigger they
/// already own; confirmation is the one-time act of accepting a trigger into the firing set in the first place —
/// distinguishing "the operator has never reviewed this" from "the operator turned it off." Both must hold for the
/// scan to look at a trigger: an unconfirmed trigger is inert <b>regardless of <c>Enabled</c></b>. This mirrors the
/// execution path's arm → review → send (R-11) and the opt-in posture of <c>DefaultEntryAction</c> (gh#218): a
/// trigger that pages an operator — or wakes the reviewer — earns the same deliberate gate before it can fire.
/// </para>
/// <para>
/// <see cref="Unconfirmed"/> is deliberately the zero value: a defaulted, reset, or corrupt row reads as inert, so
/// the fail-closed direction (no fire) is also the cheapest accident. The same zero-is-the-safe-default reasoning as
/// <see cref="TriggerArmState.Unseeded"/> — not the refusable-<c>Unknown</c> pattern, so there is no <c>&lt;&gt; 0</c>
/// refusal; the DB check instead pins the column to the known value set. A schema migration is the one place this
/// zero is <b>not</b> applied: existing operator-relied-upon triggers are backfilled to <see cref="Confirmed"/>, so a
/// column addition never silently disarms an alert already in service.
/// </para>
/// </remarks>
public enum TriggerConfirmation
{
    /// <summary>Authored but not yet accepted for live evaluation — inert regardless of <c>Enabled</c>. The default.</summary>
    Unconfirmed = 0,

    /// <summary>The operator has confirmed it for live evaluation; the scan may fire it once <c>Enabled</c>.</summary>
    Confirmed = 1,
}
