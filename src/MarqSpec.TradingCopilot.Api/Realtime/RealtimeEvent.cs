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

/// <summary>The phase of a fan-out catch-up bracket (gh#645, #690).</summary>
public enum RealtimeCatchUpPhase
{
    /// <summary>Catch-up is beginning — every event that follows, up to <see cref="Completed"/>, is HISTORY, not live.</summary>
    Started,

    /// <summary>Catch-up is done — the stream is live from here.</summary>
    Completed,
}

/// <summary>
/// Brackets a fan-out <b>catch-up</b> after a restart (gh#645, #690). When <see cref="RealtimeEventLogFanoutHost"/>
/// resumes from a committed cursor and finds a backlog — events that accrued while it was down — it broadcasts a
/// <see cref="RealtimeCatchUpPhase.Started"/>, replays the missed events as <b>history</b>, then broadcasts a
/// <see cref="RealtimeCatchUpPhase.Completed"/> and goes live. The presentation reads the bracket to render the
/// replay as a catch-up from an outage rather than as live signals, so a <b>historical</b> kill-switch / auto-flatten
/// never renders as a <b>live</b> safety banner (R-13 / R-16). It is broadcast to every connection, like the events
/// it brackets. A cold first start (no committed cursor) is not an outage: the whole log is history, so it catches
/// up silently and emits no bracket.
/// </summary>
/// <param name="Phase">Whether the catch-up is beginning or has completed.</param>
/// <param name="Sequence">
/// The cursor at this boundary — the resume point the catch-up starts <i>from</i> at
/// <see cref="RealtimeCatchUpPhase.Started"/>, and the head it caught up <i>through</i> at
/// <see cref="RealtimeCatchUpPhase.Completed"/>.
/// </param>
public sealed record RealtimeCatchUp(RealtimeCatchUpPhase Phase, long Sequence)
{
    /// <summary>The client method the hub invokes to bracket a catch-up.</summary>
    public const string ClientMethod = "realtimeCatchUp";
}
