namespace MarqSpec.TradingCopilot.Api.Notifications;

/// <summary>
/// Releases a dedup key <b>without</b> going through the delivery queue (gh#1077) — the half of a resolve that
/// nothing downstream can recover once it is lost.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DedupingNotificationChannel"/> suppresses a repeat while it holds the incident's key, and it
/// releases that key <i>only</i> through <c>ResolveAsync</c>. Above it sits
/// <see cref="QueuedNotificationChannel"/>, so a resolve that cannot be enqueued never reaches the decorator and
/// the key stays held <b>for the life of the process</b>: every later, independent incident on that key is then
/// suppressed as a duplicate while every layer reports success. That is ADR-0019's own named failure — <i>one
/// notification per process lifetime instead of one per outage</i> — and gh#1045 and gh#1051 each reproduced it
/// from a different producer.
/// </para>
/// <para>
/// <b>The two halves of a resolve fail differently, so they are separated here.</b> Cancelling the outstanding
/// page needs the transport, so it must be queued and a caller told to retry when it cannot be. Releasing the key
/// is an in-memory dictionary removal: it touches no network, cannot block, and — critically — <b>no other layer
/// remembers that it is owed</b>. There is no outbox row for a resolve and no producer that reliably retries one
/// (<c>TriggerEvaluationService</c>'s staleness recovery fires exactly once per outage), so a lost release is
/// permanent silence on that key. It is therefore performed directly, on the caller's thread, at the one moment
/// the queue has refused it.
/// </para>
/// <para>
/// <b>This is a fallback, never the ordinary path.</b> A resolve that <i>is</i> enqueued must release the key from
/// the pump, in order, behind the send it clears — releasing it early would let a concurrent escalation on the same
/// key slip past the suppression it is meant to meet. The invariant the fallback restores is the weaker, safer one:
/// a held key is released even when its cancel could not be, so the failure lands on the side of a duplicate page
/// rather than silence, exactly as <see cref="DedupingNotificationChannel.ResolveAsync"/> argues for its own
/// unconditional re-arm.
/// </para>
/// <para>
/// Deliberately <b>synchronous</b>: an implementation that needed to await something would not be usable where
/// this is used, which is the auto-flatten's thread on the R-13 path (gh#289).
/// </para>
/// </remarks>
public interface IIncidentKeyRegistry
{
    /// <summary>Releases <paramref name="dedupKey"/> so the next occurrence of that incident is reported again.</summary>
    /// <remarks>Must not block, throw, or touch a network — see the type remarks.</remarks>
    /// <param name="dedupKey">The incident key to release.</param>
    /// <returns>
    /// <see langword="true"/> when a key was actually being held (so a repeat <i>was</i> being suppressed);
    /// <see langword="false"/> when there was nothing to release, which is the ordinary case.
    /// </returns>
    bool ReleaseIncident(string dedupKey);
}
