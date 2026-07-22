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
}
