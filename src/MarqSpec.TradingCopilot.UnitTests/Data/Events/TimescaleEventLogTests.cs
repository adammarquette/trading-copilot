using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Events;

/// <summary>
/// The append-only event log behind the <see cref="IEventLog"/> seam (ADR-0001). InMemory covers the seam's
/// contract — append/read ordering, cursor mechanics, validation; the Timescale specifics (hypertable,
/// retention policy, NOTIFY trigger) are database objects, proven against live Postgres.
/// </summary>
public class TimescaleEventLogTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context()
    {
        DbContextOptions<TradingCopilotDbContext> options =
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseInMemoryDatabase(_database)
                .Options;

        return new TradingCopilotDbContext(options, new FixedUser(Guid.NewGuid()));
    }

    private static EventDraft Draft(string type = "market.quote", string payload = """{"bid":5301.25}""") =>
        new(type, "projectx", new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero), payload);

    [Fact]
    public async Task AppendAsync_ShouldAssignMonotonicSequences_AndStampRecordedAt()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);

        EventEnvelope first = await log.AppendAsync(Draft(), CancellationToken.None);
        EventEnvelope second = await log.AppendAsync(Draft(), CancellationToken.None);

        second.Sequence.Should().BeGreaterThan(first.Sequence); // the log's total order
        first.Id.Should().NotBeEmpty();
        first.RecordedAt.Should().NotBe(default);
        first.Type.Should().Be("market.quote");
    }

    [Fact]
    public async Task AppendAsync_ShouldPreserveAProducerSuppliedId_ForIdempotentProducers()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        Guid producerId = Guid.NewGuid();

        EventEnvelope appended = await log.AppendAsync(Draft() with { Id = producerId }, CancellationToken.None);

        // At-least-once delivery means consumers dedupe by event id (ADR-0001) -- a producer that retries must
        // be able to pin the id so the duplicate is recognisable.
        appended.Id.Should().Be(producerId);
    }

    [Fact]
    public async Task AppendAsync_ShouldCarryTheProducersTraceParent_AcrossTheAsyncBoundary()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        EventEnvelope appended = await log.AppendAsync(Draft() with { TraceParent = traceParent }, CancellationToken.None);
        IReadOnlyList<EventEnvelope> read = await log.ReadAfterAsync(0, 1, CancellationToken.None);

        // The log is an async boundary: the consumer continues the producer's trace via a span link, so the
        // traceparent must survive the round trip (engineering §7 -- the envelope decision recorded there).
        appended.TraceParent.Should().Be(traceParent);
        read[0].TraceParent.Should().Be(traceParent);
    }

    [Theory]
    [InlineData("", "projectx")]
    [InlineData("  ", "projectx")]
    [InlineData("market.quote", "")]
    [InlineData("market.quote", "  ")]
    public async Task AppendAsync_ShouldRejectABlankTypeOrSource(string type, string source)
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);

        Func<Task> act = () => log.AppendAsync(
            new EventDraft(type, source, DateTimeOffset.UnixEpoch, "{}"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReturnEventsAfterTheSequence_InOrder_UpToTheLimit()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope first = await log.AppendAsync(Draft(payload: """{"n":1}"""), CancellationToken.None);
        await log.AppendAsync(Draft(payload: """{"n":2}"""), CancellationToken.None);
        await log.AppendAsync(Draft(payload: """{"n":3}"""), CancellationToken.None);

        IReadOnlyList<EventEnvelope> page = await log.ReadAfterAsync(first.Sequence, limit: 2, CancellationToken.None);

        page.Should().HaveCount(2);
        page[0].Payload.Should().Be("""{"n":2}""");
        page[1].Payload.Should().Be("""{"n":3}""");
        page.Should().BeInAscendingOrder(envelope => envelope.Sequence);
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReturnNothing_WhenTheConsumerIsCaughtUp()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope only = await log.AppendAsync(Draft(), CancellationToken.None);

        (await log.ReadAfterAsync(only.Sequence, limit: 10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cursor_ShouldStartAbsent_AndReadBackWhatWasCommitted()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);

        (await log.GetCursorAsync("indicator-builder", CancellationToken.None)).Should().BeNull();

        await log.CommitCursorAsync("indicator-builder", 41, CancellationToken.None);
        (await log.GetCursorAsync("indicator-builder", CancellationToken.None)).Should().Be(41);

        // Recommit upserts -- including BACKWARDS: a deliberate cursor reset is how "a new consumer replays
        // from offset 0" (ADR-0001) generalises to rebuilds; forward-only would block them.
        await log.CommitCursorAsync("indicator-builder", 7, CancellationToken.None);
        (await log.GetCursorAsync("indicator-builder", CancellationToken.None)).Should().Be(7);
    }

    [Fact]
    public async Task Cursors_ShouldBeIndependent_PerConsumerGroup()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);

        await log.CommitCursorAsync("indicator-builder", 10, CancellationToken.None);
        await log.CommitCursorAsync("journal-projector", 3, CancellationToken.None);

        (await log.GetCursorAsync("indicator-builder", CancellationToken.None)).Should().Be(10);
        (await log.GetCursorAsync("journal-projector", CancellationToken.None)).Should().Be(3);
    }

    // --- OccurredAt is normalised to UTC before storage (regression, gh#201) ---
    //
    // The storage column is `timestamp with time zone`, and Npgsql refuses to WRITE a DateTimeOffset whose offset
    // is not zero -- the append threw. EventDraft.OccurredAt is documented as "when the event happened at the
    // source" and the domain accepts ANY offset, so the contract and the column disagreed, and the first producer
    // to emit an exchange-local timestamp would have hit it. Normalising is the fix rather than rejecting: the log
    // orders by the instant, so an offset carries no information it uses.
    //
    // These are InMemory, which does not enforce the column type -- so they cannot reproduce the ORIGINAL throw.
    // They pin the behaviour that makes it impossible instead, which is what has to hold going forward. The throw
    // itself is pinned against live Postgres by the gh#161 integration suite.

    [Fact]
    public async Task AppendAsync_ShouldNormaliseOccurredAtToUtc_WhenTheDraftCarriesANonUtcOffset()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        DateTimeOffset nonUtc = new(2026, 7, 20, 9, 30, 0, TimeSpan.FromHours(-5));

        EventEnvelope appended = await log.AppendAsync(
            new EventDraft("market.quote", "projectx", nonUtc, """{"bid":5301.25}"""), CancellationToken.None);

        appended.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task AppendAsync_ShouldPreserveTheInstant_WhenNormalisingOccurredAt()
    {
        // Normalising must move the OFFSET, never the moment. Shifting the instant would silently misdate every
        // event from a non-UTC producer -- a worse defect than the crash it replaces, because nothing would fail.
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        DateTimeOffset nonUtc = new(2026, 7, 20, 9, 30, 0, TimeSpan.FromHours(-5));

        EventEnvelope appended = await log.AppendAsync(
            new EventDraft("market.quote", "projectx", nonUtc, """{"bid":5301.25}"""), CancellationToken.None);

        appended.OccurredAt.Should().Be(nonUtc); // DateTimeOffset equality compares the instant
        appended.OccurredAt.UtcDateTime.Should().Be(new DateTime(2026, 7, 20, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task AppendAsync_ShouldLeaveOccurredAtUnchanged_WhenItIsAlreadyUtc()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        DateTimeOffset utc = new(2026, 7, 22, 14, 30, 0, TimeSpan.Zero);

        EventEnvelope appended = await log.AppendAsync(
            new EventDraft("market.quote", "projectx", utc, """{"bid":5301.25}"""), CancellationToken.None);

        appended.OccurredAt.Should().Be(utc);
        appended.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task AppendAsync_ShouldStoreTheNormalisedOccurredAt_NotOnlyReturnIt()
    {
        // The envelope is built from the stored entity, but assert the read-back too: returning a normalised
        // value while persisting the original would leave the crash in place behind a green test.
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        DateTimeOffset nonUtc = new(2026, 7, 20, 9, 30, 0, TimeSpan.FromHours(-5));

        await log.AppendAsync(
            new EventDraft("market.quote", "projectx", nonUtc, """{"bid":5301.25}"""), CancellationToken.None);

        IReadOnlyList<EventEnvelope> page = await log.ReadAfterAsync(0, 10, CancellationToken.None);

        page.Should().ContainSingle();
        page[0].OccurredAt.Offset.Should().Be(TimeSpan.Zero);
        page[0].OccurredAt.Should().Be(nonUtc);
    }
}
