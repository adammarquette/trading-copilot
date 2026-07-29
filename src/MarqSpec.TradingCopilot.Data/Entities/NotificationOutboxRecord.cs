using MarqSpec.TradingCopilot.Domain.Notifications;

namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// One notification held durably until it has actually been delivered (gh#400) — the ledger that makes a page
/// survive a crash between "the thing happened" and "the operator was told".
/// </summary>
/// <remarks>
/// <para>
/// gh#289 moved delivery off the R-13 hot path with an <b>in-process</b> queue, which was the right fix for the
/// latency problem and left a durability one: a hard crash before the pump drains loses whatever it held. This
/// row is the fix for that half — nothing is considered sent until <see cref="DeliveredAt"/> is stamped.
/// </para>
/// <para>
/// <b><see cref="DedupKey"/> is the primary key, and that is the idempotence guard.</b> A redelivery after a
/// crash collides with the existing row rather than paging twice. Doubling a page is not a cosmetic fault: a
/// pager that cries twice for one incident is one the operator learns to distrust, which is the same failure
/// ADR-0019's noise budget exists to prevent.
/// </para>
/// <para>
/// <b>Not <c>IUserOwned</c></b> — an alert belongs to the deployment, and the relay that delivers it runs as
/// background plumbing with no authenticated user (R-20's acknowledged-global category, like the event log).
/// </para>
/// </remarks>
public sealed class NotificationOutboxRecord
{
    /// <summary>This row's own identity.</summary>
    /// <remarks>
    /// A <b>surrogate</b> key, not the dedup key — and that correction matters (gh#400). Keying on
    /// <see cref="DedupKey"/> looked like free DB-enforced idempotence and actually asserted something much
    /// stronger and wrong: that an incident happens <i>once ever</i>. The second flatten failure of the day
    /// carries the same key, so it could never be recorded — the insert hit the primary key, the never-throw
    /// contract swallowed it, and the page vanished. Exactly the silent loss this outbox exists to prevent.
    /// </remarks>
    public required Guid Id { get; set; }

    /// <summary>
    /// The incident identity. Unique only among rows that are <b>still owed</b> (a filtered index) — "an incident
    /// already owed is not owed twice", without claiming it can only ever happen once. Suppressing <i>repeat</i>
    /// pages for a live incident is <c>DedupingNotificationChannel</c>'s job, with re-arm semantics the outbox has
    /// no business duplicating.
    /// </summary>
    public required string DedupKey { get; set; }

    /// <summary>How loudly this should arrive (ADR-0019's P1 / P2 / P3).</summary>
    public required NotificationSeverity Severity { get; set; }

    /// <summary>The notification title.</summary>
    public required string Title { get; set; }

    /// <summary>The notification body.</summary>
    public required string Body { get; set; }

    /// <summary>When the intent was recorded. Always UTC.</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When delivery was confirmed, or <see langword="null"/> while still owed. <b>The relay's whole work list is
    /// "rows where this is null"</b>, so a crash mid-delivery leaves the row claimable rather than lost.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// How many delivery attempts have been made. Recorded rather than inferred so a channel that keeps failing
    /// is visible as a number instead of a pattern someone has to notice in the logs.
    /// </summary>
    public int Attempts { get; set; }
}
