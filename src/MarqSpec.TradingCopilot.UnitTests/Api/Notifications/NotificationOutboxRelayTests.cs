using System.Globalization;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The relay that makes the outbox mean something (gh#437 ⇒ gh#400): rows persisted by
/// <see cref="OutboxNotificationChannel"/> are dead weight until something delivers them and marks them sent.
/// </summary>
/// <remarks>
/// The guarantee is <b>no dropped page</b>, not exactly-once — see
/// <see cref="DrainAsync_ShouldLeaveTheRowUndelivered_WhenTheDeliveryChainRefusesIt"/>. Delivery is attempted
/// first and the row stamped after, so a crash in between re-delivers rather than loses. That direction is
/// deliberate: a repeated page is an annoyance, a missed one is the failure R-13 exists to prevent.
/// </remarks>
public class NotificationOutboxRelayTests
{
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly FixedClock _clock = new(DateTimeOffset.Parse("2026-07-29T14:00:00Z", CultureInfo.InvariantCulture));

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context() => new(
        new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
        new FixedUser(Guid.NewGuid()));

    [Fact]
    public async Task DrainAsync_ShouldDeliverAndStamp_WhenAnUndeliveredRowIsWaiting()
    {
        // The whole point: a row the channel persisted must actually reach the transport, and must be marked so
        // it is not sent again on the next pass.
        await SeedAsync("flatten:9001:ES");
        RecordingChannel delivery = new();

        int delivered = await Relay(delivery).DrainAsync(CancellationToken.None);

        delivered.Should().Be(1);
        delivery.Sent.Should().ContainSingle().Which.DedupKey.Should().Be("flatten:9001:ES");
        (await DeliveredAtAsync("flatten:9001:ES")).Should().Be(_clock.GetUtcNow(), "a sent page is stamped, so it is never re-sent");
    }

    [Fact]
    public async Task DrainAsync_ShouldDeliverARowWrittenByAPreviousProcess_SoAPageSurvivesARestart()
    {
        // THE gh#400 guarantee. The row is written, the process dies before anything delivers it, and a fresh
        // process picks it up -- which is the entire reason the alert is in a table rather than in memory.
        await SeedAsync("flatten:9001:CL", createdAt: _clock.GetUtcNow().AddMinutes(-30));
        RecordingChannel delivery = new();

        // A brand-new relay over the same store is exactly what a restarted host is.
        int delivered = await Relay(delivery).DrainAsync(CancellationToken.None);

        delivered.Should().Be(1);
        delivery.Sent.Should().ContainSingle().Which.DedupKey.Should().Be("flatten:9001:CL");
    }

    [Fact]
    public async Task DrainAsync_ShouldNotResendADeliveredRow_SoAnOperatorIsPagedOncePerIncident()
    {
        // Idempotence across passes, which is what the dedup key buys: the row IS the ledger, so a second pass
        // over the same incident sends nothing.
        await SeedAsync("flatten:9001:ES");
        RecordingChannel delivery = new();
        NotificationOutboxRelay relay = Relay(delivery);

        await relay.DrainAsync(CancellationToken.None);
        int second = await relay.DrainAsync(CancellationToken.None);

        second.Should().Be(0);
        delivery.Sent.Should().ContainSingle("the second pass must find nothing owed — one page per incident");
    }

    [Fact]
    public async Task DrainAsync_ShouldLeaveTheRowUndelivered_WhenTheDeliveryChainRefusesIt()
    {
        // Fail-safe: an unaccepted page stays owed and is retried, rather than being marked sent and lost. The
        // attempt is still counted, so a permanently wedged transport is visible rather than silent.
        await SeedAsync("flatten:9001:ES");
        RecordingChannel delivery = new() { Accept = false };

        int delivered = await Relay(delivery).DrainAsync(CancellationToken.None);

        delivered.Should().Be(0);
        (await DeliveredAtAsync("flatten:9001:ES")).Should().BeNull("an unaccepted page is still owed");
        (await AttemptsAsync("flatten:9001:ES")).Should().Be(1, "the attempt is recorded so a wedged transport is discoverable");
    }

    [Fact]
    public async Task DrainAsync_ShouldKeepDraining_WhenOneDeliveryThrows()
    {
        // One poisonous row must not strand every page behind it. The flatten path may have raised several
        // incidents at once, and the second is not less urgent for the first being broken.
        await SeedAsync("flatten:9001:ES", createdAt: _clock.GetUtcNow().AddMinutes(-2));
        await SeedAsync("flatten:9001:CL", createdAt: _clock.GetUtcNow().AddMinutes(-1));
        RecordingChannel delivery = new() { ThrowOn = "flatten:9001:ES" };

        int delivered = await Relay(delivery).DrainAsync(CancellationToken.None);

        delivered.Should().Be(1);
        (await DeliveredAtAsync("flatten:9001:CL")).Should().NotBeNull("the healthy page still went out");
        (await DeliveredAtAsync("flatten:9001:ES")).Should().BeNull("and the failed one stays owed for the next pass");
    }

    [Fact]
    public async Task DrainAsync_ShouldDeliverOldestFirst_SoAnIncidentIsNotOvertakenByALaterOne()
    {
        await SeedAsync("second", createdAt: _clock.GetUtcNow().AddMinutes(-1));
        await SeedAsync("first", createdAt: _clock.GetUtcNow().AddMinutes(-5));
        RecordingChannel delivery = new();

        await Relay(delivery).DrainAsync(CancellationToken.None);

        delivery.Sent.Select(sent => sent.DedupKey).Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task DrainAsync_ShouldDoNothing_WhenTheOutboxIsEmpty()
    {
        RecordingChannel delivery = new();

        int delivered = await Relay(delivery).DrainAsync(CancellationToken.None);

        delivered.Should().Be(0);
        delivery.Sent.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------------

    private NotificationOutboxRelay Relay(INotificationChannel delivery) =>
        new(Context(), delivery, _clock, NullLogger<NotificationOutboxRelay>.Instance);

    private async Task SeedAsync(string dedupKey, DateTimeOffset? createdAt = null)
    {
        await using TradingCopilotDbContext database = Context();
        database.NotificationOutbox.Add(new NotificationOutboxRecord
        {
            Id = Guid.CreateVersion7(),
            DedupKey = dedupKey,
            Severity = NotificationSeverity.Page,
            Title = $"Auto-flatten escalated — {dedupKey}",
            Body = "Still exposed after 3 close attempts.",
            CreatedAt = createdAt ?? _clock.GetUtcNow(),
            DeliveredAt = null,
            Attempts = 0,
        });
        await database.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> DeliveredAtAsync(string dedupKey)
    {
        await using TradingCopilotDbContext database = Context();
        return (await database.NotificationOutbox.SingleAsync(row => row.DedupKey == dedupKey)).DeliveredAt;
    }

    private async Task<int> AttemptsAsync(string dedupKey)
    {
        await using TradingCopilotDbContext database = Context();
        return (await database.NotificationOutbox.SingleAsync(row => row.DedupKey == dedupKey)).Attempts;
    }

    /// <summary>A clock pinned to one instant — enough to assert the delivered stamp without a new dependency.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A delivery chain that records what it was asked to send, and can refuse or throw on demand.</summary>
    private sealed class RecordingChannel : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];

        public bool Accept { get; init; } = true;

        public string? ThrowOn { get; init; }

        public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            if (notification.DedupKey == ThrowOn)
            {
                throw new InvalidOperationException("transport wedged (test double)");
            }

            Sent.Add(notification);
            return Task.FromResult(Accept);
        }

        public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
