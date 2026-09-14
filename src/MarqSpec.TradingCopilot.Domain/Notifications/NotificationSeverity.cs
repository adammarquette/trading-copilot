namespace MarqSpec.TradingCopilot.Domain.Notifications;

/// <summary>
/// How loudly a notification should arrive (gh#243, ADR-0019's P1/P2/P3 taxonomy).
/// </summary>
/// <remarks>
/// Severity is stated in <b>operator-outcome</b> terms — wake me, tell me, record it — not in any channel's
/// vocabulary. An adapter maps these onto whatever its transport offers; the seam must not learn Pushover's
/// priority numbers or Discord's mention semantics, or swapping the channel would mean rewriting every caller.
/// </remarks>
public enum NotificationSeverity
{
    /// <summary>
    /// Record it; do not interrupt. Delivered silently — the flatten <i>confirmation</i> PRD P1 asks for, at zero
    /// fatigue cost. Deliberately the zero value: an unset severity is the harmless one.
    /// </summary>
    Quiet,

    /// <summary>
    /// Tell me, once. Needs attention today but not this minute — a tier that failed while a backstop caught it,
    /// or protection that degraded without being lost. Suppressed outside market hours.
    /// </summary>
    Notify,

    /// <summary>
    /// <b>Wake me.</b> Money is at risk right now. Repeats until <i>acknowledged</i> and bypasses Do Not Disturb —
    /// an on-call-of-one needs a pager, not a notification. Never quiet-hour suppressed.
    /// </summary>
    Page,
}
