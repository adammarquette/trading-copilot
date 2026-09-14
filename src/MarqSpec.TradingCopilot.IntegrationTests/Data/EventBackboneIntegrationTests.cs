using System.Text.Json;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Events;
using MarqSpec.TradingCopilot.IntegrationTests.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.TradingCopilot.IntegrationTests.Data;

/// <summary>
/// Storage-tier integration coverage for the event backbone (ADR-0001, gh#13 / gh#98, first producer gh#156)
/// against the <b>applied</b> <c>AddEventBackbone</c> migration on <c>timescale/timescaledb-ha:pg17</c>. The
/// unit tests (<c>Data/Events/TimescaleEventLogTests</c>) prove the contract in memory; this proves the
/// <b>storage</b> — the DDL the migration performs (drop the PK, add the sequence index, convert to a hypertable,
/// a 24h retention policy, the <c>NOTIFY</c> trigger) that no in-memory backend can observe.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="IEventLog"/> and <see cref="TradingCopilotDbContext"/> directly — this subsystem has
/// no HTTP surface. <see cref="Event"/> is deliberately <b>not</b> operator-owned (an acknowledged global in the
/// R-20 guard), so reads need no <c>IgnoreQueryFilters</c>.
/// </para>
/// <para>
/// <b>Out of scope (noted for visibility):</b> the migration's graceful-degradation arm — where the timescaledb
/// extension is absent it <c>RAISE WARNING</c>s and leaves <c>Events</c> a plain table — is never exercised here,
/// because the test image <i>is</i> a Timescale image. Covering the dead arm needs a plain-postgres container; a
/// follow-up rather than a widening of this suite. Traces R-1 · ADR-0001 · Engineering guide §5, §7.
/// </para>
/// </remarks>
public class EventBackboneIntegrationTests : IClassFixture<PostgresApiFactory>
{
    private readonly PostgresApiFactory _factory;

    public EventBackboneIntegrationTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Append_ShouldRoundTripEnvelope_ThroughTheAppliedMigration()
    {
        Guid id = Guid.NewGuid();
        // UTC: the storage column is `timestamp with time zone`, which only accepts UTC on write — a non-UTC
        // OccurredAt is normalised to it on append (see Append_ShouldNormaliseToUtc_… / gh#201).
        DateTimeOffset occurredAt = new(2026, 7, 20, 14, 30, 0, TimeSpan.Zero);
        const string payload = "{\"symbol\":\"MES\",\"bid\":5000.25}";
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        EventEnvelope returned = await AppendAsync(
            Draft("market.quote", "projectx", payload, id: id, occurredAt: occurredAt, traceParent: traceParent));

        returned.Sequence.Should().BePositive("the identity generator assigns a sequence on append");

        Event stored = await QueryDbAsync(db =>
            db.Events.AsNoTracking().SingleAsync(e => e.Sequence == returned.Sequence));

        stored.Id.Should().Be(id);
        stored.Type.Should().Be("market.quote");
        stored.Source.Should().Be("projectx");
        stored.TraceParent.Should().Be(traceParent, "trace context rides the envelope across the async boundary (§7)");

        // Payload survives as jsonb — valid JSON with its values intact (jsonb may reorder keys / renormalise
        // whitespace, so parse and compare semantically rather than byte-for-byte).
        using JsonDocument parsed = JsonDocument.Parse(stored.Payload);
        parsed.RootElement.GetProperty("symbol").GetString().Should().Be("MES");
        parsed.RootElement.GetProperty("bid").GetDecimal().Should().Be(5000.25m);

        // The source instant round-trips exactly, as UTC.
        stored.OccurredAt.ToUniversalTime().Should().Be(occurredAt.ToUniversalTime());
        stored.OccurredAt.Offset.Should().Be(TimeSpan.Zero, "the timestamptz column stores and returns UTC");
        stored.RecordedAt.Should().BeAfter(occurredAt, "the log stamps RecordedAt at append time, later than the source instant");
    }

    [Fact]
    public async Task Append_ShouldNormaliseToUtc_WhenOccurredAtCarriesANonUtcOffset()
    {
        // The gh#201 regression guard, FLIPPED from its pin (contract §2). This previously asserted the observed
        // defect — the append threw, because Events."OccurredAt" is `timestamp with time zone` and Npgsql refuses
        // to WRITE a non-zero offset — with a note that the fix would turn it red. gh#201 landed: the log now
        // normalises to UTC on append, so this asserts the fixed behaviour against live Postgres.
        //
        // Both halves matter. The offset must be gone (or the write fails), AND the instant must be untouched:
        // normalising that shifted the moment would silently misdate every event from a non-UTC producer, which
        // is worse than the crash it replaced because nothing would fail.
        DateTimeOffset nonUtc = new(2026, 7, 20, 9, 30, 0, TimeSpan.FromHours(-5));

        EventEnvelope returned = await AppendAsync(Draft(occurredAt: nonUtc));

        Event stored = await QueryDbAsync(db =>
            db.Events.AsNoTracking().SingleAsync(e => e.Sequence == returned.Sequence));

        stored.OccurredAt.Offset.Should().Be(TimeSpan.Zero, "the timestamptz column stores and returns UTC");
        stored.OccurredAt.Should().Be(nonUtc, "normalising moves the offset, never the instant");
        stored.OccurredAt.UtcDateTime.Should().Be(new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Append_ShouldAssignMonotonicSequences_UnderConcurrentAppends()
    {
        const int writers = 20;

        // Each append runs in its OWN scope (its own DbContext + IEventLog) — genuinely concurrent producers.
        // The PK is gone; only the identity generator stands between the log and duplicate sequence numbers.
        IEnumerable<Task<EventEnvelope>> appends = Enumerable
            .Range(0, writers)
            .Select(i => AppendAsync(Draft(payload: $"{{\"n\":{i}}}")));

        EventEnvelope[] results = await Task.WhenAll(appends);

        long[] sequences = [.. results.Select(r => r.Sequence)];
        sequences.Should().OnlyHaveUniqueItems("concurrent appends must never share a sequence — that is the total order consumers commit by");
        sequences.Should().AllSatisfy(s => s.Should().BePositive());
        sequences.Should().HaveCount(writers);
    }

    [Fact]
    public async Task Append_ShouldStoreTwoRowsSharingOneId_WhenAProducerReplaysTheSameEvent()
    {
        // A websocket reconnect replays an overlapping window: the producer pins the SAME id so the duplicate is
        // recognisable. Id is deliberately not DB-unique, so the replay must land a SECOND row (two sequences,
        // one id) and NOT throw — consumers collapse it by id. This is the test that would go red if someone
        // "helpfully" added a unique index on Id, turning a benign reconnect into a crashing append.
        Guid replayedId = Guid.NewGuid();
        EventDraft draft = Draft("market.quote", "projectx", "{\"symbol\":\"MES\",\"bid\":5000.25}", id: replayedId);

        EventEnvelope first = await AppendAsync(draft);
        EventEnvelope second = await AppendAsync(draft);

        first.Id.Should().Be(replayedId);
        second.Id.Should().Be(replayedId);
        second.Sequence.Should().NotBe(first.Sequence, "each physical append gets its own sequence");

        List<Event> rows = await QueryDbAsync(db =>
            db.Events.AsNoTracking().Where(e => e.Id == replayedId).ToListAsync());

        rows.Should().HaveCount(2, "the replay lands a second row rather than throwing on a unique-id violation");
        rows.Select(r => r.Sequence).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Append_ShouldGenerateAnId_WhenTheDraftSuppliesNone()
    {
        EventEnvelope first = await AppendAsync(Draft(id: null));
        EventEnvelope second = await AppendAsync(Draft(id: null));

        first.Id.Should().NotBe(Guid.Empty, "the log assigns an id when the producer supplies none (draft.Id ?? Guid.NewGuid())");
        second.Id.Should().NotBe(Guid.Empty);
        first.Id.Should().NotBe(second.Id, "two id-less appends must not collide");
    }

    [Fact]
    public async Task ReadAfter_ShouldReturnEventsInSequenceOrder_AndRespectTheLimit()
    {
        // Append a contiguous batch (tests within a class run sequentially, so nothing interleaves).
        EventEnvelope[] batch = [
            await AppendAsync(Draft(payload: "{\"n\":1}")),
            await AppendAsync(Draft(payload: "{\"n\":2}")),
            await AppendAsync(Draft(payload: "{\"n\":3}")),
            await AppendAsync(Draft(payload: "{\"n\":4}")),
            await AppendAsync(Draft(payload: "{\"n\":5}")),
        ];
        long[] seq = [.. batch.Select(e => e.Sequence)];

        // Page 1: the first three of my batch, in ascending sequence order.
        IReadOnlyList<EventEnvelope> page1 = await ReadAfterAsync(seq[0] - 1, limit: 3);
        page1.Select(e => e.Sequence).Should().Equal(seq[0], seq[1], seq[2]);

        // Page 2 resumes exactly where page 1 ended — no skip, no repeat — and the limit is honoured.
        IReadOnlyList<EventEnvelope> page2 = await ReadAfterAsync(page1[^1].Sequence, limit: 3);
        page2.Select(e => e.Sequence).Should().Equal(seq[3], seq[4]);
    }

    [Fact]
    public async Task ReadAfter_ShouldReturnEmpty_WhenTheCursorIsAtTheHead()
    {
        EventEnvelope head = await AppendAsync(Draft());

        IReadOnlyList<EventEnvelope> after = await ReadAfterAsync(head.Sequence, limit: 10);

        after.Should().BeEmpty("a caught-up consumer reading after the newest sequence gets nothing");
    }

    [Fact]
    public async Task CommitCursor_ShouldInsertThenUpdate_ForTheSameConsumerGroup()
    {
        // Distinct group names so the shared container's other tests never collide with this one.
        string group = $"group-{Guid.NewGuid():N}";
        string other = $"group-{Guid.NewGuid():N}";

        (await GetCursorAsync(group)).Should().BeNull("a group that has never committed has no cursor");

        // Insert branch.
        await CommitCursorAsync(group, 10);
        (await GetCursorAsync(group)).Should().Be(10);
        DateTimeOffset firstCommittedAt = await CommittedAtAsync(group);

        // Update branch — the same row moves forward, a second row is NOT inserted.
        await CommitCursorAsync(group, 25);
        (await GetCursorAsync(group)).Should().Be(25);
        (await QueryDbAsync(db => db.EventCursors.CountAsync(c => c.ConsumerGroup == group)))
            .Should().Be(1, "committing twice upserts the one cursor row, never accumulates");
        (await CommittedAtAsync(group)).Should().BeOnOrAfter(firstCommittedAt, "CommittedAt advances with each commit");

        // A second group is tracked independently.
        await CommitCursorAsync(other, 5);
        (await GetCursorAsync(other)).Should().Be(5);
        (await GetCursorAsync(group)).Should().Be(25, "committing one group must not disturb another");
    }

    [Fact]
    public async Task Append_ShouldRejectBlankTypeOrSource_BeforeTouchingTheDatabase()
    {
        long before = await QueryDbAsync(db => db.Events.LongCountAsync());

        Func<Task> blankType = () => AppendAsync(Draft(type: "   "));
        Func<Task> blankSource = () => AppendAsync(Draft(source: ""));

        await blankType.Should().ThrowAsync<ArgumentException>("an event needs a non-blank type");
        await blankSource.Should().ThrowAsync<ArgumentException>("an event needs a non-blank source");

        long after = await QueryDbAsync(db => db.Events.LongCountAsync());
        after.Should().Be(before, "the guards reject before the row is added — nothing is left behind");
    }

    // -- Retention-gap signal (gh#228, flipping the gh#162 pin now that gh#227 shipped) ------------------------
    // The gh#227 fix makes ReadAfterAsync SIGNAL a retention gap (EventPage.Gap) instead of silently resuming.
    // The gap decision is a row-existence query (`Sequence <= cursor` still exists?), NOT a chunk read — so a
    // DELETE of the oldest rows reproduces the EXACT state a Timescale chunk-drop leaves, faithfully exercising
    // the same decision path. A genuine `drop_chunks` end-to-end case (isolated container) lives in
    // RetentionChunkDropIntegrationTests.

    [Fact]
    public async Task RetentionGap_ShouldSignalGap_WhenACursorTrailsTheDroppedWindow()
    {
        // THE flipped gh#162 pin (was RetentionGap_ShouldSkipSilently…): a consumer whose cursor trails the
        // dropped window must be TOLD, not silently resumed.
        long[] seq = await AppendBatchAsync(5);
        await DeleteThroughAsync(seq[2]); // retention drops the oldest three

        EventPage page = await ReadPageAsync(seq[0] - 1, limit: 10);

        page.Gap.Should().NotBeNull("a cursor trailing the dropped window is signalled, never silently resumed (gh#227)");
        page.Events.Select(e => e.Sequence).Should().Equal(
            new[] { seq[3], seq[4] }, "the survivors are still returned — a gap degrades what the consumer knows, not its progress");
    }

    [Fact]
    public async Task ReadAfter_ShouldCarryActionableDetail_WhenGapSignalled()
    {
        // The signal must carry enough to decide: the cursor read from, and the oldest sequence that survives.
        long[] seq = await AppendBatchAsync(5);
        await DeleteThroughAsync(seq[2]);

        EventPage page = await ReadPageAsync(seq[0] - 1, limit: 10);

        page.Gap!.RequestedAfterSequence.Should().Be(seq[0] - 1, "the gap reports the cursor the consumer asked from");
        page.Gap.OldestAvailableSequence.Should().Be(seq[3], "and the oldest sequence still available after the drop");
    }

    [Fact]
    public async Task ReadAfter_ShouldNotSignalGap_WhenCursorIsInsideWindow()
    {
        // The common path: nothing dropped, the window is intact — clean and silent (and it never pays the
        // gap-check round trip).
        long[] seq = await AppendBatchAsync(4);

        EventPage page = await ReadPageAsync(seq[0] - 1, limit: 10);

        page.Gap.Should().BeNull("an intact window never signals a gap");
        page.Events.Select(e => e.Sequence).Should().Equal(seq, "every appended event is returned in order");
    }

    [Fact]
    public async Task ReadAfter_ShouldNotSignalGap_WhenConsumerGroupIsBrandNew()
    {
        // A brand-new consumer reads from 0 (no committed cursor) — that is not a gap even if old rows were
        // dropped, because it never held a position to fall behind.
        long[] seq = await AppendBatchAsync(5);
        await DeleteThroughAsync(seq[2]);

        EventPage page = await ReadPageAsync(afterSequence: 0, limit: 10);

        page.Gap.Should().BeNull("reading from the start of what survives is not a gap");
        page.Events.Select(e => e.Sequence).Should().Equal(new[] { seq[3], seq[4] });
    }

    [Fact]
    public async Task ReadAfter_ShouldNotSignalGap_WhenCaughtUpAtTheHead()
    {
        // Caught up: an empty page is "no new events", never a dropped window.
        long[] seq = await AppendBatchAsync(1);

        EventPage page = await ReadPageAsync(seq[0], limit: 10);

        page.Events.Should().BeEmpty();
        page.Gap.Should().BeNull("no new events is not a gap");
    }

    [Fact]
    public async Task ReadAfter_ShouldSignalGapOnce_WhenConsumerResumesAfterGap()
    {
        // The gap fires while the cursor trails the window; once the consumer advances to a surviving sequence the
        // next read is clean — not a gap on every subsequent poll.
        long[] seq = await AppendBatchAsync(5);
        await DeleteThroughAsync(seq[2]);

        EventPage first = await ReadPageAsync(seq[0] - 1, limit: 10);
        first.Gap.Should().NotBeNull("the first read after the drop signals the gap");

        EventPage resumed = await ReadPageAsync(seq[3], limit: 10); // advanced onto a surviving sequence
        resumed.Gap.Should().BeNull("resuming from inside the surviving window is clean");
        resumed.Events.Select(e => e.Sequence).Should().Equal(new[] { seq[4] });
    }

    [Fact]
    public async Task ReadAfter_ShouldStillSignalGap_WhenConsumerRestartsMidGap()
    {
        // The signal is derived from durable state (the cursor vs. what survives), not in-memory — so it survives
        // a process boundary: a consumer that restarts still learns the gap on its next read.
        long[] seq = await AppendBatchAsync(5);
        await DeleteThroughAsync(seq[2]);

        EventPage before = await ReadPageAsync(seq[0] - 1, limit: 10);
        before.Gap.Should().NotBeNull();

        // Each ReadPageAsync opens its own scope — a fresh scope models the restart, carrying no in-memory state.
        EventPage afterRestart = await ReadPageAsync(seq[0] - 1, limit: 10);
        afterRestart.Gap.Should().NotBeNull("the gap is durable — re-derived from the log, not remembered");
        afterRestart.Gap!.OldestAvailableSequence.Should().Be(seq[3]);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers — each opens its own scope so appends are genuinely independent (and concurrency-safe).
    // ---------------------------------------------------------------------------------------------------------

    private static EventDraft Draft(
        string type = "market.quote",
        string source = "projectx",
        string payload = "{\"symbol\":\"MES\"}",
        Guid? id = null,
        string? traceParent = null,
        DateTimeOffset? occurredAt = null)
    {
        return new EventDraft(type, source, occurredAt ?? new DateTimeOffset(2026, 7, 20, 14, 30, 0, TimeSpan.Zero), payload)
        {
            Id = id,
            TraceParent = traceParent,
        };
    }

    private async Task<EventEnvelope> AppendAsync(EventDraft draft)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.AppendAsync(draft, CancellationToken.None);
    }

    private async Task<IReadOnlyList<EventEnvelope>> ReadAfterAsync(long afterSequence, int limit)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return (await log.ReadAfterAsync(afterSequence, limit, CancellationToken.None)).Events;
    }

    /// <summary>Reads the full page — the retention-gap signal (<see cref="EventPage.Gap"/>) is what gh#228 asserts,
    /// so unlike <see cref="ReadAfterAsync"/> this keeps it. A fresh scope per call also models a consumer restart.</summary>
    private async Task<EventPage> ReadPageAsync(long afterSequence, int limit)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.ReadAfterAsync(afterSequence, limit, CancellationToken.None);
    }

    /// <summary>Appends <paramref name="count"/> contiguous events and returns their sequences.</summary>
    private async Task<long[]> AppendBatchAsync(int count)
    {
        List<long> sequences = [];
        for (int n = 1; n <= count; n++)
        {
            EventEnvelope appended = await AppendAsync(Draft(payload: $"{{\"n\":{n}}}"));
            sequences.Add(appended.Sequence);
        }

        return [.. sequences];
    }

    /// <summary>Deletes every row at or below <paramref name="throughSequence"/> — the state a retention chunk-drop
    /// leaves (the gap decision reads row existence, not chunk metadata, so this is faithful, not a simulation).</summary>
    private Task DeleteThroughAsync(long throughSequence) => ExecuteDbAsync(db =>
        db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Events\" WHERE \"Sequence\" <= {throughSequence}"));

    private async Task<long?> GetCursorAsync(string consumerGroup)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        return await log.GetCursorAsync(consumerGroup, CancellationToken.None);
    }

    private async Task CommitCursorAsync(string consumerGroup, long sequence)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IEventLog log = scope.ServiceProvider.GetRequiredService<IEventLog>();
        await log.CommitCursorAsync(consumerGroup, sequence, CancellationToken.None);
    }

    private Task<DateTimeOffset> CommittedAtAsync(string consumerGroup) => QueryDbAsync(db =>
        db.EventCursors.AsNoTracking().Where(c => c.ConsumerGroup == consumerGroup).Select(c => c.CommittedAt).SingleAsync());

    private async Task<T> QueryDbAsync<T>(Func<TradingCopilotDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        return await query(db);
    }

    private async Task ExecuteDbAsync(Func<TradingCopilotDbContext, Task> action)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        TradingCopilotDbContext db = scope.ServiceProvider.GetRequiredService<TradingCopilotDbContext>();
        await action(db);
    }
}
