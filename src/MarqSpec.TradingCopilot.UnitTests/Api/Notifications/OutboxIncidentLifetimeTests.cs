using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// How long a dedup key stays spent (gh#458). The outbox suppresses a duplicate page, which is the point — but the
/// suppression has to expire when the incident does. These guards pin the boundary: <b>still owed</b> means
/// suppress, <b>already delivered</b> means a fresh occurrence is a fresh page.
/// </summary>
/// <remarks>
/// The defect these were written against: <c>DedupKey</c> was the primary key while the relay marked delivery by
/// stamping <c>DeliveredAt</c> and keeping the row — so a delivered row held its key forever and the second
/// occurrence of any incident could never be inserted. On the R-13 path that means a repeat auto-flatten failure
/// is silently unreportable *because the first one was reported successfully*.
/// </remarks>
public class OutboxIncidentLifetimeTests
{
    private const string IncidentKey = "flatten-failed:11111111-1111-1111-1111-111111111111";

    private static DateTimeOffset Monday { get; } = new(2026, 7, 27, 18, 45, 0, TimeSpan.Zero);

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly AdvanceableClock _clock = new(Monday);
    private readonly INotificationChannel _delivery = A.Fake<INotificationChannel>();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    /// <summary>A clock the test can move, so "the same failure a day later" is expressible without waiting.</summary>
    private sealed class AdvanceableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private TradingCopilotDbContext Context() =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(Guid.Empty));

    /// <summary>
    /// A real container over the same store, because <c>SendAsync</c> records through a scope it opens itself
    /// (gh#452) — so the guards have to go through a scope factory, not a hand-built context.
    /// </summary>
    private ServiceProvider Compose()
    {
        ServiceCollection services = new();
        services.AddScoped<ICurrentUser>(_ => new FixedUser(Guid.Empty));
        services.AddDbContext<TradingCopilotDbContext>(options => options.UseInMemoryDatabase(_database));

        return services.BuildServiceProvider();
    }

    private OutboxNotificationChannel Channel(IServiceProvider provider, TradingCopilotDbContext callers) =>
        new(callers, provider.GetRequiredService<IServiceScopeFactory>(), _clock, _delivery,
            NullLogger<OutboxNotificationChannel>.Instance);

    private static Notification Incident(string body) =>
        new(NotificationSeverity.Page, "Auto-flatten failed", body, IncidentKey);

    [Fact]
    public async Task SendAsync_ShouldRecordTheSecondOccurrence_WhenTheFirstWasAlreadyDelivered()
    {
        await using ServiceProvider provider = Compose();

        // Monday: the incident is raised, and the relay delivers it. The row stays, stamped.
        await using (TradingCopilotDbContext monday = Context())
        {
            await Channel(provider, monday).SendAsync(Incident("first failure"), CancellationToken.None);

            NotificationOutboxRecord owed = await monday.NotificationOutbox.SingleAsync();
            owed.DeliveredAt = _clock.GetUtcNow();
            await monday.SaveChangesAsync();
        }

        // Tuesday: the SAME failure happens again. It is not less urgent for having happened before.
        _clock.Advance(TimeSpan.FromDays(1));

        await using TradingCopilotDbContext tuesday = Context();
        bool recorded = await Channel(provider, tuesday)
            .SendAsync(Incident("second failure"), CancellationToken.None);

        recorded.Should().BeTrue("a repeat of a closed incident is a new incident, and the operator must be told");
        (await tuesday.NotificationOutbox.CountAsync(row => row.DeliveredAt == null))
            .Should().Be(1, "the second occurrence must be owed, not swallowed by the first one's history");
    }

    [Fact]
    public async Task SendAsync_ShouldNotRecordTwice_WhenTheIncidentIsStillOwed()
    {
        // The guarantee that must SURVIVE the fix: while a page is outstanding, re-raising the same condition
        // must not queue a second one. Doubling a page is what teaches an operator to distrust the pager.
        await using ServiceProvider provider = Compose();
        await using TradingCopilotDbContext database = Context();
        OutboxNotificationChannel channel = Channel(provider, database);

        await channel.SendAsync(Incident("first"), CancellationToken.None);
        bool second = await channel.SendAsync(Incident("still failing"), CancellationToken.None);

        second.Should().BeTrue("the incident is recorded and will be delivered — that is success for the caller");
        (await database.NotificationOutbox.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_ShouldNotRecordTwice_WhenTheIncidentIsOwedInAnotherScope()
    {
        // The same guarantee with NOTHING shared between the two raises — separate channel instances, separate
        // caller contexts, the real production shape where the flatten and the watchdog each have their own scope.
        // Before gh#458 the primary key caught this, but by throwing: the channel logged "will NOT be delivered"
        // and reported failure to a caller whose page was in fact already owed.
        await using ServiceProvider provider = Compose();

        await using (TradingCopilotDbContext first = Context())
        {
            await Channel(provider, first).SendAsync(Incident("first"), CancellationToken.None);
        }

        await using TradingCopilotDbContext second = Context();
        bool again = await Channel(provider, second)
            .SendAsync(Incident("still failing"), CancellationToken.None);

        again.Should().BeTrue("the incident is already owed, so the caller's page is not lost");
        (await second.NotificationOutbox.CountAsync()).Should().Be(1, "…and it is not queued twice");
    }

    [Fact]
    public async Task EnlistAsync_ShouldStageNothing_WhenAnEarlierPassLeftTheIncidentOwed()
    {
        // gh#455's defect, caught by the existing flatten integration suite. Sharing a transaction cuts both ways:
        // a staged row that violates the filtered unique index surfaces on the PRODUCER's SaveChanges and takes
        // their write down with it. It really happened — pass 1 escalated and left the incident owed, pass 2 staged
        // the same key, and the duplicate aborted the flatten.missed journal entry.
        //
        // Local cannot see the earlier row: a different scope committed it and that context is long gone. So the
        // enlister must ASK the database, which is why the seam is async.
        await using ServiceProvider provider = Compose();

        await using (TradingCopilotDbContext earlierPass = Context())
        {
            await Channel(provider, earlierPass).EnlistAsync(Incident("first pass"), CancellationToken.None);
            await earlierPass.SaveChangesAsync();
        }

        await using TradingCopilotDbContext laterPass = Context();
        await Channel(provider, laterPass).EnlistAsync(Incident("second pass"), CancellationToken.None);

        laterPass.ChangeTracker.Entries<NotificationOutboxRecord>().Should().BeEmpty(
            "staging a duplicate would abort the producer's own save — the page must not endanger the state it reports");

        Func<Task> save = () => laterPass.SaveChangesAsync();
        await save.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_ShouldRecordSeparately_WhenTwoDifferentIncidentsAreOwedAtOnce()
    {
        await using ServiceProvider provider = Compose();
        await using TradingCopilotDbContext database = Context();
        OutboxNotificationChannel channel = Channel(provider, database);

        await channel.SendAsync(Incident("flatten"), CancellationToken.None);
        await channel.SendAsync(
            new Notification(NotificationSeverity.Page, "Kill switch engaged", "manual", "kill-switch:engaged"),
            CancellationToken.None);

        (await database.NotificationOutbox.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// The schema half, asserted against a <b>relational</b> model because the filter is a relational concept —
    /// InMemory silently ignores indexes, so the behavioural guards above cannot witness this on their own.
    /// </summary>
    [Fact]
    public void NotificationOutbox_ShouldKeyOnTheSurrogate_AndScopeDedupToUndeliveredRows()
    {
        // Never connects — building the model is offline. UseVector matches production (gh#109): without it the
        // model itself will not build, because EmbeddingRecord.Embedding has no mapping.
        using TradingCopilotDbContext relational = new(
            new DbContextOptionsBuilder<TradingCopilotDbContext>()
                .UseNpgsql("Host=not-connected;Database=model-only", npgsql => npgsql.UseVector())
                .Options,
            new FixedUser(Guid.Empty));

        IEntityType outbox = relational.Model.FindEntityType(typeof(NotificationOutboxRecord))!;

        outbox.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal([nameof(NotificationOutboxRecord.Id)],
                "a delivered row must not hold its dedup key against the next occurrence");

        IIndex dedup = outbox.GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(NotificationOutboxRecord.DedupKey)]));

        dedup.IsUnique.Should().BeTrue("idempotence stays enforced by the database, not by the relay remembering");
        dedup.GetFilter().Should().Contain("DeliveredAt",
            "…but only among rows still owed — otherwise it is the old defect wearing an index");
    }
}
