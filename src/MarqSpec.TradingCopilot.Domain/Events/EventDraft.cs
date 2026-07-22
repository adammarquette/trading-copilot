namespace MarqSpec.TradingCopilot.Domain.Events;

/// <summary>
/// What a producer submits to the event log (ADR-0001): the envelope minus what the log itself assigns
/// (sequence, recorded-at).
/// </summary>
/// <param name="Type">The event type (e.g. <c>market.quote</c>). Namespaced, dot-separated by convention.</param>
/// <param name="Source">The producing system (e.g. <c>projectx</c>).</param>
/// <param name="OccurredAt">When the event happened at the source — distinct from when the log recorded it.</param>
/// <param name="Payload">The event body as JSON. Stored as <c>jsonb</c>; the log does not interpret it.</param>
public sealed record EventDraft(string Type, string Source, DateTimeOffset OccurredAt, string Payload)
{
    /// <summary>
    /// Optional producer-supplied id. Delivery is at-least-once and consumers dedupe by event id (ADR-0001), so
    /// a retrying producer pins the id to make its duplicate recognisable. Left null, the log assigns one.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>
    /// Optional W3C <c>traceparent</c> of the producing operation. The log is an async boundary, so trace
    /// context rides the envelope and the consumer continues the trace via a span link — one trace spans
    /// <c>ingest → append → process → write</c> (engineering §7; the envelope decision recorded there).
    /// </summary>
    public string? TraceParent { get; init; }
}
