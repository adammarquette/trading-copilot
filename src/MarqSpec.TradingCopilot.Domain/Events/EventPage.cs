namespace MarqSpec.TradingCopilot.Domain.Events;

/// <summary>
/// What a consumer's cursor missed because retention dropped it (gh#227, decision gh#162, ADR-0001).
/// </summary>
/// <remarks>
/// The span is an <b>upper bound on what was lost, not an exact count</b>. Sequence numbers are not contiguous —
/// a rolled-back append consumes a value — so the numbers between the two bounds may never have existed. Reporting
/// the bounds rather than a fabricated "N events lost" keeps the signal honest (engineering §9: never fabricate).
/// </remarks>
/// <param name="RequestedAfterSequence">The cursor the consumer read from — the last sequence it fully processed.</param>
/// <param name="OldestAvailableSequence">The oldest sequence that still survives; everything between is gone.</param>
public sealed record EventRetentionGap(long RequestedAfterSequence, long OldestAvailableSequence);

/// <summary>
/// A page of events read from the backbone, plus whatever the read learned about <b>what it could not return</b>
/// (gh#227).
/// </summary>
/// <remarks>
/// <para>
/// A typed result rather than a thrown exception, deliberately. A retention gap is an <i>expected, recoverable</i>
/// condition on a lossy log, not an exceptional one — and an exception on a polling loop invites the
/// <c>catch</c> that re-hides it, which is the defect gh#227 exists to close. As a field on the result, ignoring
/// the gap is a visible choice in the consumer's code rather than an absence.
/// </para>
/// <para>
/// The events are returned <b>either way</b>. A gap degrades what the consumer knows, not its ability to make
/// progress; withholding the survivors would turn a reporting improvement into an outage on a safety path.
/// </para>
/// </remarks>
/// <param name="Events">The events after the requested cursor, in sequence order; empty when caught up.</param>
/// <param name="Gap">The retention gap in front of the cursor, or <see langword="null"/> when none was detected.</param>
public sealed record EventPage(IReadOnlyList<EventEnvelope> Events, EventRetentionGap? Gap)
{
    /// <summary>A page carrying no gap — the ordinary read.</summary>
    /// <param name="events">The events read.</param>
    /// <returns>The page.</returns>
    public static EventPage Of(IReadOnlyList<EventEnvelope> events) => new(events, null);
}
