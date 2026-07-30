namespace MarqSpec.TradingCopilot.Domain.Notifications;

/// <summary>
/// Records a notification <b>inside the caller's own unit of work</b> (gh#455) — the stronger of the two ways
/// into the outbox, for producers where a dropped page has real consequence.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="INotificationChannel.SendAsync"/> writes and commits on its own, which is right for almost
/// everything: producers stay unchanged and the page survives a crash before delivery. What it cannot close is
/// the gap between the producer's <i>own</i> commit and that write — by the time the seam is called, the
/// producer's transaction has already closed, so a crash in between leaves the state change durable and the page
/// gone.
/// </para>
/// <para>
/// This seam closes it by not being a separate write at all. The row joins whatever the producer is already
/// about to save, so intent and state change commit <b>together or not at all</b>. On the R-13 auto-flatten path
/// that is the difference between "the escalation is journalled and the operator was never told" and a state
/// that cannot exist.
/// </para>
/// <para>
/// <b>The caller must save.</b> Enlisting and then never calling <c>SaveChangesAsync</c> records nothing — no
/// exception, no page. That is the cost of joining someone else's transaction, and it is why this is used
/// deliberately by named producers rather than being the default path.
/// </para>
/// <para>
/// <b>And nothing it stages may fail the caller's save</b> (gh#455). Sharing a transaction cuts both ways: a row
/// that violates a constraint takes the producer's own write down with it. That is why this checks for an
/// already-owed incident before staging rather than leaving the outbox's unique index to catch it — the index is
/// a sound backstop for <c>SendAsync</c>, which catches its own failure and returns, and a catastrophic one here,
/// where the exception surfaces on the auto-flatten's journal write and aborts the pass. It cost a real defect to
/// learn: an escalation on one pass left the incident owed, the next pass staged the same key, and the duplicate
/// killed the <c>flatten.missed</c> journal entry.
/// </para>
/// </remarks>
public interface INotificationEnlister
{
    /// <summary>
    /// Stages the notification in the caller's pending work <b>without saving</b> — and never stages one that
    /// would fail their save.
    /// </summary>
    /// <remarks>
    /// Asynchronous because it <b>reads</b>: it must know whether this incident is already owed, which the
    /// caller's own change tracker cannot answer for a row committed by an earlier pass. It never writes and never
    /// commits, so it remains safe to call inside an open transaction.
    /// </remarks>
    /// <param name="notification">The notification to record.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task EnlistAsync(Notification notification, CancellationToken cancellationToken);
}
