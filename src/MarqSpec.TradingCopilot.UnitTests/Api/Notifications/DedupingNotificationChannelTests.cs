using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// One push per incident, not one per poll (gh#243, ADR-0019 §*The noise budget*).
/// </summary>
/// <remarks>
/// <para>
/// This is the guard the whole channel lives or dies by. The auto-flatten scheduler re-emits its escalation
/// roughly <b>every 15 s</b> while exposure persists and the watchdog every 20 s, so a 30-minute outage is ~120
/// events. Forwarded naively that is ~120 pushes — and a pager that cries 120 times gets muted, at which point
/// it is strictly worse than no pager, because it manufactures confidence.
/// </para>
/// <para>
/// Dedup lives in a decorator rather than inside the Pushover adapter so that every future adapter (Discord
/// gh#100, web push ADR-0010) inherits it instead of reimplementing it — the ADR said "in the adapter", and this
/// is that requirement met one layer out, where it composes.
/// </para>
/// </remarks>
public class DedupingNotificationChannelTests
{
    private readonly INotificationChannel _inner = A.Fake<INotificationChannel>();

    public DedupingNotificationChannelTests()
    {
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
    }

    private DedupingNotificationChannel Channel() =>
        new(_inner, NullLogger<DedupingNotificationChannel>.Instance);

    private static Notification Note(
        NotificationSeverity severity = NotificationSeverity.Page,
        string key = "flatten:9001:ES",
        string title = "Auto-flatten escalated") =>
        new(severity, title, "body", key);

    [Fact]
    public async Task SendAsync_ShouldForwardTheFirstNotification_ForADedupKey()
    {
        await Channel().SendAsync(Note(), CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldSendOnce_WhenTheSameConditionRepeats()
    {
        // The centrepiece: 120 repeats of the same incident must reach the operator once.
        DedupingNotificationChannel channel = Channel();

        for (int i = 0; i < 120; i++)
        {
            await channel.SendAsync(Note(), CancellationToken.None);
        }

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldSendSeparately_ForDifferentDedupKeys()
    {
        // ES escalating must not suppress GC escalating -- they are different incidents on different money.
        DedupingNotificationChannel channel = Channel();

        await channel.SendAsync(Note(key: "flatten:9001:ES"), CancellationToken.None);
        await channel.SendAsync(Note(key: "flatten:9001:GC"), CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldSendAgain_AfterTheIncidentResolves()
    {
        // A NEW occurrence of the same condition is a new incident and must page again. Suppressing it would mean
        // the second failure of the day goes unreported -- the exact silence this system exists to remove.
        DedupingNotificationChannel channel = Channel();

        await channel.SendAsync(Note(), CancellationToken.None);
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.SendAsync(Note(), CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldForwardToTheInnerChannel_SoAnOutstandingPageIsCancelled()
    {
        DedupingNotificationChannel channel = Channel();
        await channel.SendAsync(Note(), CancellationToken.None);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotForward_WhenNothingWasSentForThatKey()
    {
        await Channel().ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldNotSuppress_WhenTheFirstAttemptFailedToSend()
    {
        // A failed push never reached the operator, so it must not count as "already told them" -- otherwise one
        // transient outage silently costs the whole incident.
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(false).Once()
            .Then.Returns(true);
        DedupingNotificationChannel channel = Channel();

        await channel.SendAsync(Note(), CancellationToken.None);
        await channel.SendAsync(Note(), CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldDedupeIndependentlyOfSeverity_ForTheSameKey()
    {
        // The key identifies the incident; a severity change on the same incident is not a new reason to wake up.
        DedupingNotificationChannel channel = Channel();

        await channel.SendAsync(Note(NotificationSeverity.Page), CancellationToken.None);
        await channel.SendAsync(Note(NotificationSeverity.Notify), CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }
}
