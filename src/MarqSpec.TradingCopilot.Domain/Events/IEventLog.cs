namespace MarqSpec.TradingCopilot.Domain.Events;

/// <summary>
/// The event-backbone seam (ADR-0001): an append-only, replayable log with per-consumer-group cursors.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so the backbone is an adapter choice, not an architecture: today it is a TimescaleDB
/// hypertable; a future NATS JetStream / Kafka swap is an adapter change — the same discipline as the venue
/// abstraction (R-17).
/// </para>
/// <para>
/// Delivery is <b>at-least-once</b>: consumers read forward from their cursor, process idempotently (dedupe by
/// <see cref="EventEnvelope.Id"/>), and commit the sequence they finished. Committing <i>backwards</i> is
/// legitimate — a deliberate cursor reset is how "a new consumer replays from the start" generalises to
/// rebuilds. A push-style wake-up (<c>LISTEN/NOTIFY</c>) arrives with the first live consumer; until then
/// consumers poll <see cref="ReadAfterAsync"/>.
/// </para>
/// </remarks>
public interface IEventLog
{
    /// <summary>Appends an event and returns it with the log-assigned sequence and recorded-at.</summary>
    /// <param name="draft">The event to append.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stored envelope.</returns>
    Task<EventEnvelope> AppendAsync(EventDraft draft, CancellationToken cancellationToken);

    /// <summary>Reads up to <paramref name="limit"/> events strictly after a sequence, in sequence order.</summary>
    /// <remarks>
    /// <b>The log is lossy past its retention window</b> (ADR-0001), so the page also reports whether the cursor
    /// had fallen behind it — see <see cref="EventPage.Gap"/>. A consumer must handle a gap explicitly: a silent
    /// resume at the head is exactly the defect gh#227 closed, because a hole is otherwise indistinguishable from
    /// an uneventful poll.
    /// </remarks>
    /// <param name="afterSequence">The consumer's cursor; <c>0</c> reads from the start of what survives.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The next page of events (empty when caught up) and any retention gap in front of the cursor.</returns>
    Task<EventPage> ReadAfterAsync(long afterSequence, int limit, CancellationToken cancellationToken);

    /// <summary>Gets a consumer group's committed cursor, or null when the group has never committed.</summary>
    /// <param name="consumerGroup">The consumer group name.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The last committed sequence, or null.</returns>
    Task<long?> GetCursorAsync(string consumerGroup, CancellationToken cancellationToken);

    /// <summary>Commits a consumer group's cursor — an upsert; backwards commits are deliberate resets.</summary>
    /// <param name="consumerGroup">The consumer group name.</param>
    /// <param name="sequence">The last fully-processed sequence.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    Task CommitCursorAsync(string consumerGroup, long sequence, CancellationToken cancellationToken);
}
