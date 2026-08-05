using MarqSpec.TradingCopilot.Domain.Events;

namespace MarqSpec.TradingCopilot.Api.Realtime;

/// <summary>
/// One event pushed to a connected client over the realtime hub (gh#645, R-10). A thin projection of the durable
/// <see cref="EventEnvelope"/>: it carries the total-order <see cref="Sequence"/> the client resumes from and
/// dedupes on, the <see cref="Type"/> discriminator it switches on, and the raw JSON <see cref="Payload"/> it
/// parses. The hub is presentation-only, so this is read-side data — never a command.
/// </summary>
public sealed record RealtimeEvent(long Sequence, string Type, DateTimeOffset OccurredAt, string Payload)
{
    /// <summary>The client method the hub invokes to deliver an event (SignalR camel-cases it on the wire).</summary>
    public const string ClientMethod = "realtimeEvent";

    /// <summary>Projects a durable envelope onto the wire shape. The dedupe id and traceparent stay server-side.</summary>
    public static RealtimeEvent From(EventEnvelope envelope) =>
        new(envelope.Sequence, envelope.Type, envelope.OccurredAt, envelope.Payload);
}

/// <summary>
/// Told to a resuming client whose named sequence has fallen off the back of the log's 24h retention window
/// (gh#645, ADR-0001). It is <b>not</b> an error: the client is missing events between its cursor and
/// <see cref="OldestAvailableSequence"/>, so it must re-fetch current state over REST rather than assume the
/// replayed tail is complete. The realtime stream stays live either way.
/// </summary>
public sealed record RealtimeGap(
    long RequestedAfterSequence, long OldestAvailableSequence, DateTimeOffset OldestAvailableOccurredAt)
{
    /// <summary>The client method the hub invokes to report a retention gap.</summary>
    public const string ClientMethod = "realtimeGap";

    /// <summary>Projects the log's typed gap onto the wire shape.</summary>
    public static RealtimeGap From(EventRetentionGap gap) =>
        new(gap.RequestedAfterSequence, gap.OldestAvailableSequence, gap.OldestAvailableOccurredAt);
}
