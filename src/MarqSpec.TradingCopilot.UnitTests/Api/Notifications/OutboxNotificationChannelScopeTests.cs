using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// Regression guards for gh#452: <c>SendAsync</c> must record through its <b>own</b> scope, never the caller's.
/// </summary>
/// <remarks>
/// <para>
/// The two entry points want opposite things, and conflating them is the defect. <c>Enlist</c> shares the caller's
/// unit of work deliberately — intent and state change commit atomically, which is the whole reason the outbox
/// exists. <c>SendAsync</c> must be independent of it.
/// </para>
/// <para>
/// Writing through the caller's context did two things. It committed whatever the producer had pending, at a point
/// the producer never chose. And — the reason this is safety-critical — a failure anywhere in the producer's unit
/// of work surfaced on <i>this</i> <c>SaveChanges</c>, where the channel's never-throw contract caught it, logged
/// it, and returned <see langword="false"/>: the operator is never paged, and the cause is an unrelated pending
/// change. gh#437 made that live for every producer by binding this channel as the seam.
/// </para>
/// <para>
/// Found on <c>feature/400_notification-outbox-relay</c>, whose commit reports it made four alerting tests report
/// an empty inbox. These are the unit-level guards for it.
/// </para>
/// </remarks>
public class OutboxNotificationChannelScopeTests
{
    private readonly string _database = Guid.NewGuid().ToString();

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task SendAsync_ShouldNotCommitTheCallersPendingChanges_WhenItRecordsAPage()
    {
        // A producer mid-unit-of-work raises an alert. Recording the page must not push the producer's own staged
        // work to the database — that is a partial commit at a moment the producer never chose.
        (ServiceProvider provider, TradingCopilotDbContext callers) = Compose();
        await using ServiceProvider _ = provider;

        callers.Firms.Add(new Firm { Id = Guid.NewGuid(), Name = "half-finished", Type = FirmType.PropFirm });

        bool recorded = await Channel(provider, callers).SendAsync(Page(), CancellationToken.None);

        recorded.Should().BeTrue("the page is durably recorded");
        (await CountFirmsAsync()).Should().Be(
            0, "the caller's pending work must still be pending — SendAsync commits only its own row");
        (await CountOutboxAsync()).Should().Be(1, "and the page is there, written through the channel's own scope");
    }

    [Fact]
    public async Task SendAsync_ShouldStillRecordThePage_WhenTheCallersPendingChangesWouldFailToSave()
    {
        // THE dangerous one. A producer with a doomed pending change raises an alert. On the shared context that
        // SaveChanges threw, the never-throw contract swallowed it, and the page was silently lost — on the R-13
        // path, which is the one place a missing page is the failure the system exists to prevent.
        (ServiceProvider provider, TradingCopilotDbContext callers) = Compose();
        await using ServiceProvider _ = provider;

        // A row that cannot be saved: a required navigation left null violates the model on SaveChanges.
        callers.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Instrument = null!, // required — the caller's unit of work is doomed
            Side = OrderSide.Buy,
            Size = 1,
            EntryPrice = 5_000m,
            Mode = TradingMode.Practice,
        });

        bool recorded = await Channel(provider, callers).SendAsync(Page(), CancellationToken.None);

        recorded.Should().BeTrue(
            "the page must be recorded regardless of the caller's unrelated failure — sharing the context let that "
            + "failure be swallowed by the never-throw contract and silently eat the page (gh#452)");
        (await CountOutboxAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Enlist_ShouldStillShareTheCallersContext_BecauseThatIsThePoint()
    {
        // The other half must NOT change. Enlist exists so intent and state change commit together; if it opened
        // its own scope the atomicity gh#400 was built for would be gone, and the fix would have traded one
        // dropped-page window for another.
        (ServiceProvider provider, TradingCopilotDbContext callers) = Compose();
        await using ServiceProvider _ = provider;

        await Channel(provider, callers).EnlistAsync(Page(), CancellationToken.None);

        (await CountOutboxAsync()).Should().Be(0, "Enlist stages only — nothing is committed until the caller saves");

        await callers.SaveChangesAsync();

        (await CountOutboxAsync()).Should().Be(1, "and it lands with the caller's own commit, atomically");
    }

    // ---------------------------------------------------------------------------------------------------------

    private static Notification Page() =>
        new(NotificationSeverity.Page, "Auto-flatten escalated — ES", "Still exposed.", "flatten:9001:ES");

    private OutboxNotificationChannel Channel(IServiceProvider provider, TradingCopilotDbContext callers) => new(
        callers,
        provider.GetRequiredService<IServiceScopeFactory>(),
        new FixedClock(DateTimeOffset.UnixEpoch),
        new NullNotificationChannel(NullLogger<NullNotificationChannel>.Instance),
        NullLogger<OutboxNotificationChannel>.Instance);

    /// <summary>A provider whose scopes hand out contexts over one shared in-memory store, plus a caller's context.</summary>
    private (ServiceProvider Provider, TradingCopilotDbContext Callers) Compose()
    {
        ServiceCollection services = new();
        services.AddScoped<ICurrentUser>(_ => new FixedUser(Guid.NewGuid()));
        services.AddDbContext<TradingCopilotDbContext>(options => options.UseInMemoryDatabase(_database));

        ServiceProvider provider = services.BuildServiceProvider();
        return (provider, NewContext());
    }

    private TradingCopilotDbContext NewContext() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
        new FixedUser(Guid.NewGuid()));

    private async Task<int> CountOutboxAsync()
    {
        await using TradingCopilotDbContext database = NewContext();
        return await database.NotificationOutbox.CountAsync();
    }

    private async Task<int> CountFirmsAsync()
    {
        await using TradingCopilotDbContext database = NewContext();
        return await database.Firms.IgnoreQueryFilters().CountAsync();
    }
}
