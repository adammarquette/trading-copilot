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
/// <b>Idempotence is enforced by the database, and it expires with the incident</b> (gh#458). A unique index on
/// <see cref="DedupKey"/> <i>filtered to rows still owed</i> makes a re-raise while a page is outstanding collide
/// rather than double up — a pager that cries twice for one incident is one the operator learns to distrust, which
/// is the failure ADR-0019's noise budget exists to prevent. But the suppression must end when the incident does:
/// <see cref="DedupKey"/> was originally the <i>primary</i> key, and because the relay marks delivery by stamping
/// <see cref="DeliveredAt"/> and keeping the row, a delivered row held its key <b>forever</b> — the second
/// occurrence of any incident could never be recorded, so a repeat auto-flatten failure was unreportable
/// precisely <i>because</i> the first one was reported. Hence the surrogate <see cref="Id"/>.
/// </para>
/// <para>
/// <b>Not <c>IUserOwned</c></b> — an alert belongs to the deployment, and the relay that delivers it runs as
/// background plumbing with no authenticated user (R-20's acknowledged-global category, like the event log).
/// </para>
/// </remarks>
public sealed class NotificationOutboxRecord
{
    /// <summary>
    /// The row identity. A surrogate rather than <see cref="DedupKey"/>, so an incident's key is spent only while
    /// that occurrence is owed (gh#458). Time-ordered (<c>Guid.CreateVersion7</c>) so the relay's oldest-first
    /// read stays index-friendly.
    /// </summary>
    public required Guid Id { get; set; }

    /// <summary>
    /// The incident identity — unique among rows still owed, so one open incident is one page. Not unique across
    /// delivered history: the same condition recurring next week is a new incident.
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
