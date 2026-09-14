using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Events;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.TradingCopilot.UnitTests.Data.Events;

/// <summary>
/// The retention-gap signal on the event backbone (gh#227, decision gh#162, ADR-0001). The log is <b>lossy past
/// its window</b>: retention drops chunks older than 24h, so a consumer whose cursor trails that window silently
/// resumed at the head — a hole indistinguishable from an uneventful poll. Both consumers on this log ride
/// safety-critical paths (stop promotion gh#153, conditional firing gh#198), where a silent hole leaves derived
/// state confidently wrong rather than merely stale.
/// </summary>
public class EventRetentionGapTests
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

    private static EventDraft Draft(string payload = """{"bid":5301.25}""") =>
        new("market.quote", "projectx", new DateTimeOffset(2026, 7, 22, 14, 30, 0, TimeSpan.Zero), payload);

    /// <summary>Simulates the retention policy, which is a background job we cannot wait on in a unit test.</summary>
    private async Task DropThroughAsync(long sequence)
    {
        await using TradingCopilotDbContext context = Context();
        List<Event> doomed = await context.Events.Where(candidate => candidate.Sequence <= sequence).ToListAsync();
        context.Events.RemoveRange(doomed);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportNoGap_WhenTheCursorIsInsideTheWindow()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope first = await log.AppendAsync(Draft(), CancellationToken.None);
        await log.AppendAsync(Draft(), CancellationToken.None);

        EventPage page = await log.ReadAfterAsync(first.Sequence, limit: 10, CancellationToken.None);

        page.Gap.Should().BeNull();
        page.Events.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportAGap_WhenEverythingAtOrBelowTheCursorWasDropped()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope[] appended =
        [
            await log.AppendAsync(Draft("""{"n":1}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":2}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":3}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":4}"""), CancellationToken.None),
        ];

        // The consumer processed the first event; retention then dropped the first three.
        long cursor = appended[0].Sequence;
        await DropThroughAsync(appended[2].Sequence);

        EventPage page = await log.ReadAfterAsync(cursor, limit: 10, CancellationToken.None);

        // The survivors still come back -- the read is not an error, it is a read WITH A SIGNAL. Losing the page
        // would turn a reporting improvement into an outage.
        page.Events.Select(e => e.Sequence).Should().Equal(appended[3].Sequence);

        page.Gap.Should().NotBeNull();
        page.Gap!.RequestedAfterSequence.Should().Be(cursor);
        page.Gap.OldestAvailableSequence.Should().Be(appended[3].Sequence);
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportNoGap_WhenTheConsumerGroupIsBrandNew()
    {
        // A fresh group reads "from the start", and the start is whatever survives. Reporting that as a gap would
        // fire on every new consumer forever -- the boy-who-cried-wolf failure that gets a real signal ignored.
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope[] appended =
        [
            await log.AppendAsync(Draft("""{"n":1}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":2}"""), CancellationToken.None),
        ];
        await DropThroughAsync(appended[0].Sequence);

        EventPage page = await log.ReadAfterAsync(0, limit: 10, CancellationToken.None);

        page.Gap.Should().BeNull();
        page.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportNoGap_WhenTheLogIsEmpty()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);

        EventPage page = await log.ReadAfterAsync(500, limit: 10, CancellationToken.None);

        page.Events.Should().BeEmpty();
        page.Gap.Should().BeNull();
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportNoGap_WhenTheConsumerIsCaughtUp()
    {
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope only = await log.AppendAsync(Draft(), CancellationToken.None);

        EventPage page = await log.ReadAfterAsync(only.Sequence, limit: 10, CancellationToken.None);

        page.Events.Should().BeEmpty();
        page.Gap.Should().BeNull();
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldReportNoGap_WhenSequenceNumbersMerelySkipAheadOfTheCursor()
    {
        // The false positive this design exists to avoid. Sequence numbers are NOT contiguous: a rolled-back
        // append consumes a value, so a hole in the numbering is normal and means nothing was lost. What matters
        // is whether the cursor still sits INSIDE the surviving window -- so the check asks that, rather than
        // inferring loss from a numbering skip.
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope first = await log.AppendAsync(Draft("""{"n":1}"""), CancellationToken.None);
        EventEnvelope second = await log.AppendAsync(Draft("""{"n":2}"""), CancellationToken.None);
        EventEnvelope third = await log.AppendAsync(Draft("""{"n":3}"""), CancellationToken.None);

        // Delete a MIDDLE row: the numbering now skips, but the cursor's own event still survives.
        await using (TradingCopilotDbContext surgery = Context())
        {
            Event middle = await surgery.Events.SingleAsync(e => e.Sequence == second.Sequence);
            surgery.Events.Remove(middle);
            await surgery.SaveChangesAsync();
        }

        EventPage page = await log.ReadAfterAsync(first.Sequence, limit: 10, CancellationToken.None);

        page.Events.Select(e => e.Sequence).Should().Equal(third.Sequence);
        page.Gap.Should().BeNull("a numbering skip is not data loss — the cursor is still inside the window");
    }

    [Fact]
    public async Task ReadAfterAsync_ShouldStopReportingTheGap_OnceTheConsumerResumes()
    {
        // "Reported once per occurrence": after the consumer commits a cursor inside the surviving window, the
        // next poll is ordinary. A signal that repeated every poll would be noise the operator learns to skip.
        await using TradingCopilotDbContext context = Context();
        TimescaleEventLog log = new(context);
        EventEnvelope[] appended =
        [
            await log.AppendAsync(Draft("""{"n":1}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":2}"""), CancellationToken.None),
            await log.AppendAsync(Draft("""{"n":3}"""), CancellationToken.None),
        ];
        await DropThroughAsync(appended[1].Sequence);

        EventPage gapped = await log.ReadAfterAsync(appended[0].Sequence, limit: 10, CancellationToken.None);
        gapped.Gap.Should().NotBeNull();

        // The consumer resumes at what it just read.
        EventPage resumed = await log.ReadAfterAsync(appended[2].Sequence, limit: 10, CancellationToken.None);

        resumed.Gap.Should().BeNull();
    }
}
