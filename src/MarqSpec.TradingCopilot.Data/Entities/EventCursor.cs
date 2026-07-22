namespace MarqSpec.TradingCopilot.Data.Entities;

/// <summary>
/// A consumer group's replay cursor over the event log (ADR-0001): the last fully-processed sequence.
/// </summary>
/// <remarks>
/// One row per consumer group — groups are independent, so a new consumer starts absent (reads from the start)
/// and a rebuild is a deliberate backwards commit. System plumbing, not a workspace row — an acknowledged
/// global in the R-20 guard.
/// </remarks>
public class EventCursor
{
    /// <summary>The consumer group name — the key.</summary>
    public required string ConsumerGroup { get; set; }

    /// <summary>The last fully-processed sequence.</summary>
    public long LastSequence { get; set; }

    /// <summary>When the cursor was last committed.</summary>
    public DateTimeOffset CommittedAt { get; set; }
}
