using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.Data.Events;

/// <summary>
/// The TimescaleDB implementation of the <see cref="IEventLog"/> seam (ADR-0001): an append-only hypertable
/// plus a per-consumer-group cursor table.
/// </summary>
/// <remarks>
/// The Timescale specifics — hypertable partitioning, the retention policy, the <c>NOTIFY</c> trigger — are
/// database objects created by the <c>AddEventBackbone</c> migration (degrading gracefully to a plain table
/// where the extension is unavailable); this class is deliberately provider-plain EF so the contract is
/// testable in memory.
/// </remarks>
public sealed class TimescaleEventLog : IEventLog
{
    private readonly TradingCopilotDbContext _database;

    /// <summary>Creates the log over the scoped database.</summary>
    /// <param name="database">The database.</param>
    public TimescaleEventLog(TradingCopilotDbContext database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public async Task<EventEnvelope> AppendAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Type))
        {
            throw new ArgumentException("An event needs a non-blank type.", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.Source))
        {
            throw new ArgumentException("An event needs a non-blank source.", nameof(draft));
        }

        Event stored = new()
        {
            Id = draft.Id ?? Guid.NewGuid(),
            Type = draft.Type,
            Source = draft.Source,
            // Normalised, not rejected (gh#201). The column is `timestamp with time zone` and Npgsql refuses to
            // WRITE a non-zero offset to it, so an exchange-local source timestamp threw on append. Rejecting
            // instead would break the documented contract -- OccurredAt is "when the event happened at the
            // source", any offset -- and push the burden onto every producer. Normalising loses nothing the log
            // uses: it orders by the instant, and an offset carries no information beyond it.
            OccurredAt = draft.OccurredAt.ToUniversalTime(),
            RecordedAt = DateTimeOffset.UtcNow,
            Payload = draft.Payload,
            TraceParent = draft.TraceParent,
        };

        _database.Events.Add(stored);
        await _database.SaveChangesAsync(cancellationToken);

        return ToEnvelope(stored);
    }

    /// <inheritdoc />
    public async Task<EventPage> ReadAfterAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        List<Event> page = await _database.Events
            .Where(candidate => candidate.Sequence > afterSequence)
            .OrderBy(candidate => candidate.Sequence)
            .Take(limit)
            .ToListAsync(cancellationToken);

        EventEnvelope[] events = [.. page.Select(ToEnvelope)];

        // A fresh group (cursor 0) reads from the start of whatever survives -- by definition not a gap, or every
        // new consumer would report one forever. Nothing read means nothing to compare against: an empty log and
        // a caught-up cursor are both ordinary, and a cursor stranded in front of an EMPTY log is reported on the
        // next read that actually returns something (gh#227).
        if (afterSequence <= 0 || events.Length == 0)
        {
            return EventPage.Of(events);
        }

        long oldestAvailable = events[0].Sequence;

        // The cheap gate, so the common path costs NO second round trip: when the next surviving sequence is the
        // very next number, nothing can be missing. Only a numbering hole is worth asking the database about, and
        // a hole is rare -- appends are contiguous unless one rolled back.
        if (oldestAvailable <= afterSequence + 1)
        {
            return EventPage.Of(events);
        }

        // The hole is real only if the cursor has fallen off the BACK of the window. Asking "does anything at or
        // below the cursor still exist?" answers that directly -- and, unlike inferring loss from the numbering,
        // it does not fire on a rolled-back sequence value, where the cursor's own event is still there.
        bool cursorStillInsideWindow = await _database.Events
            .AnyAsync(candidate => candidate.Sequence <= afterSequence, cancellationToken);

        return cursorStillInsideWindow
            ? EventPage.Of(events)
            : new EventPage(events, new EventRetentionGap(afterSequence, oldestAvailable));
    }

    /// <inheritdoc />
    public async Task<long?> GetCursorAsync(string consumerGroup, CancellationToken cancellationToken)
    {
        EventCursor? cursor = await _database.EventCursors
            .FirstOrDefaultAsync(candidate => candidate.ConsumerGroup == consumerGroup, cancellationToken);

        return cursor?.LastSequence;
    }

    /// <inheritdoc />
    public async Task CommitCursorAsync(string consumerGroup, long sequence, CancellationToken cancellationToken)
    {
        EventCursor? cursor = await _database.EventCursors
            .FirstOrDefaultAsync(candidate => candidate.ConsumerGroup == consumerGroup, cancellationToken);

        if (cursor is null)
        {
            cursor = new EventCursor { ConsumerGroup = consumerGroup };
            _database.EventCursors.Add(cursor);
        }

        cursor.LastSequence = sequence;
        cursor.CommittedAt = DateTimeOffset.UtcNow;
        await _database.SaveChangesAsync(cancellationToken);
    }

    private static EventEnvelope ToEnvelope(Event stored) =>
        new(
            stored.Id,
            stored.Sequence,
            stored.Type,
            stored.Source,
            stored.OccurredAt,
            stored.RecordedAt,
            stored.Payload,
            stored.TraceParent);
}
