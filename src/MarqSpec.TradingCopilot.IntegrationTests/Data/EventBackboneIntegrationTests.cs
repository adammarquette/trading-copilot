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
        // UTC: the storage column is `timestamp with time zone`, which only accepts UTC on write (a non-UTC
        // OccurredAt is rejected outright — see Append_ShouldReject_WhenOccurredAtCarriesANonUtcOffset / gh#201).
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
    public async Task Append_ShouldReject_WhenOccurredAtCarriesANonUtcOffset()
    {
        // DEFECT gh#201 (work:code) — surfaced by this independent suite. EventDraft.OccurredAt is a
        // DateTimeOffset the domain accepts with ANY offset ("when the event happened at the source"), but the
        // Events."OccurredAt" column is `timestamp with time zone` and Npgsql refuses to WRITE a non-UTC offset
        // to it, so the append throws. QuoteIngestionService passes quote.Timestamp raw and deliberately
        // propagates append failures (#156), so a venue emitting an exchange-local timestamp would drop the
        // quote rather than store it. Pinned per contract §2: when gh#201 normalises OccurredAt to UTC the
        // append will succeed and THIS TEST GOES RED — the reminder to flip it to assert the stored instant.
        DateTimeOffset nonUtc = new(2026, 7, 20, 9, 30, 0, TimeSpan.FromHours(-5));

        Func<Task> append = () => AppendAsync(Draft(occurredAt: nonUtc));

        (await append.Should().ThrowAsync<DbUpdateException>("the storage boundary rejects a non-UTC OccurredAt"))
            .WithInnerException<ArgumentException>(
                "Npgsql refuses a non-UTC DateTimeOffset for a `timestamp with time zone` column");
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

    [Fact]
    public async Task RetentionGap_ShouldSkipSilently_WhenACursorTrailsTheDroppedWindow()
    {
        // OBSERVED gh#162 — a PIN, not a guard, and NOT counted as coverage. Retention drops chunks past the
        // window; a consumer whose cursor trails the dropped window cannot distinguish "no new events" from
        // "events were dropped". The retention policy is a background job (24h), so this simulates the drop by
        // deleting the oldest rows directly, then asserts what ReadAfterAsync ACTUALLY does today. It pins that
        // behaviour until gh#162 decides whether the reader should instead learn events were dropped — at which
        // point this flips into that issue's regression guard.
        EventEnvelope[] batch = [
            await AppendAsync(Draft(payload: "{\"n\":1}")),
            await AppendAsync(Draft(payload: "{\"n\":2}")),
            await AppendAsync(Draft(payload: "{\"n\":3}")),
            await AppendAsync(Draft(payload: "{\"n\":4}")),
            await AppendAsync(Draft(payload: "{\"n\":5}")),
        ];
        long[] seq = [.. batch.Select(e => e.Sequence)];
        long droppedThrough = seq[2]; // simulate retention dropping the oldest three

        await ExecuteDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Events\" WHERE \"Sequence\" <= {droppedThrough}"));

        // The consumer's cursor sits BELOW the dropped window.
        IReadOnlyList<EventEnvelope> read = await ReadAfterAsync(seq[0] - 1, limit: 10);

        // Observed: it silently returns only the survivors — no exception, no gap signal.
        read.Select(e => e.Sequence).Should().Equal(new[] { seq[3], seq[4] },
            "OBSERVED gh#162: the dropped sequences are skipped with no signal that they ever existed");
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
        return await log.ReadAfterAsync(afterSequence, limit, CancellationToken.None);
    }

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
