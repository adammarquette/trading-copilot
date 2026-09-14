namespace MarqSpec.TradingCopilot.Domain.Events;

/// <summary>
/// An event as it sits in the append-only log (ADR-0001) — the draft plus what the log assigned.
/// </summary>
/// <param name="Id">The event's id — the consumer-side dedupe key (delivery is at-least-once).</param>
/// <param name="Sequence">The log's monotonic sequence — the total order consumers read and commit by.</param>
/// <param name="Type">The event type (e.g. <c>market.quote</c>).</param>
/// <param name="Source">The producing system.</param>
/// <param name="OccurredAt">When the event happened at the source.</param>
/// <param name="RecordedAt">When the log recorded it — the hypertable's time dimension.</param>
/// <param name="Payload">The event body as JSON.</param>
/// <param name="TraceParent">The producer's W3C <c>traceparent</c>, when tracing — consumers span-link to it.</param>
public sealed record EventEnvelope(
    Guid Id,
    long Sequence,
    string Type,
    string Source,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string Payload,
    string? TraceParent);
