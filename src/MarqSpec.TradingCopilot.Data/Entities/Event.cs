namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A row in the append-only event log (ADR-0001) — the storage shape of the domain's <c>EventEnvelope</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> operator-owned: the log carries system plumbing (market data, venue events), not
/// workspace rows — an acknowledged global in the R-20 guard. Short-lived by design: a Timescale retention
/// policy drops chunks past the window (24h default); the long-lived record is the poller's clean historical
/// store, not this log.
/// </para>
/// <para>
/// <see cref="Sequence"/> (identity) is the <b>logical</b> key and totally orders the log. Physically the table
/// carries <i>no</i> primary key once converted to a hypertable — a hypertable's unique constraints must
/// include the time dimension, and an append-only log EF never updates needs no DB-enforced key; the identity
/// generator supplies uniqueness. Global uniqueness of <see cref="Id"/> is likewise not DB-enforced — consumers
/// dedupe by id, which at-least-once delivery requires of them anyway.
/// </para>
/// </remarks>
public class Event
{
    /// <summary>The log's monotonic sequence (identity) — the order consumers read and commit by.</summary>
    public long Sequence { get; set; }

    /// <summary>The event id — the consumer-side dedupe key; producer-supplied for idempotent retries.</summary>
    public Guid Id { get; set; }

    /// <summary>The event type (e.g. <c>market.quote</c>).</summary>
    public required string Type { get; set; }

    /// <summary>The producing system (e.g. <c>projectx</c>).</summary>
    public required string Source { get; set; }

    /// <summary>When the event happened at the source.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When the log recorded it — the hypertable's time dimension.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The event body as JSON (<c>jsonb</c>). The log does not interpret it.</summary>
    public required string Payload { get; set; }

    /// <summary>The producer's W3C <c>traceparent</c>, when tracing (engineering §7) — consumers span-link to it.</summary>
    public string? TraceParent { get; set; }
}
