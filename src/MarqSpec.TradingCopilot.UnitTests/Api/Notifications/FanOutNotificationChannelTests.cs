using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// The fan-out that lets Pushover and Discord sit in the one transport slot of the outbox → queue → dedup chain
/// (gh#1157, ADR-0019). Each lane keeps its <b>own</b> dedup memory: with one shared memory, a Discord success
/// would mark the incident "told" and a Pushover page that had failed would never be retried — the pager
/// silenced by the advisory copy.
/// </summary>
public class FanOutNotificationChannelTests
{
    private static Notification Note(string key = "flatten:9001:ES") =>
        new(NotificationSeverity.Page, "Auto-flatten escalated", "ES still exposed.", key);

    private static DedupingNotificationChannel Lane(INotificationChannel inner) =>
        new(inner, NullLogger<DedupingNotificationChannel>.Instance);

    private static INotificationChannel Transport(bool sends = true)
    {
        INotificationChannel fake = A.Fake<INotificationChannel>();
        A.CallTo(() => fake.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(sends);
        A.CallTo(() => fake.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        return fake;
    }

    [Fact]
    public async Task SendAsync_ShouldDeliverTheSameNotificationToEveryLane_AndReportAccepted()
    {
        INotificationChannel pushover = Transport();
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);
        Notification note = Note();

        bool sent = await fanOut.SendAsync(note, CancellationToken.None);

        sent.Should().BeTrue();
        A.CallTo(() => pushover.SendAsync(note, CancellationToken.None)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.SendAsync(note, CancellationToken.None)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldReportAccepted_WhenOnlyOneLaneDelivers()
    {
        FanOutNotificationChannel fanOut = new([Lane(Transport(sends: false)), Lane(Transport())]);

        (await fanOut.SendAsync(Note(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ShouldReportNotAccepted_WhenNoLaneDelivers()
    {
        FanOutNotificationChannel fanOut = new([Lane(Transport(sends: false)), Lane(Transport(sends: false))]);

        (await fanOut.SendAsync(Note(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_ShouldStillReachTheOtherLane_WhenOneLaneFails()
    {
        INotificationChannel failing = Transport(sends: false);
        INotificationChannel working = Transport();
        FanOutNotificationChannel fanOut = new([Lane(failing), Lane(working)]);

        await fanOut.SendAsync(Note(), CancellationToken.None);

        A.CallTo(() => working.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldCollapseARepeatedIncidentKey_ForEveryLane()
    {
        // The dedup chain still holds across both transports: the auto-flatten re-emits every ~15 s.
        INotificationChannel pushover = Transport();
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);

        bool first = await fanOut.SendAsync(Note(), CancellationToken.None);
        bool repeat = await fanOut.SendAsync(Note(), CancellationToken.None);

        first.Should().BeTrue();
        repeat.Should().BeFalse("both lanes already reported this incident");
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldRetryOnlyTheLaneThatFailed_WhenTheIncidentRepeats()
    {
        // The reason for per-lane memory. Pushover (the pager) fails once; Discord succeeds. The repeat must
        // reach Pushover again and must NOT re-post to Discord.
        INotificationChannel pushover = A.Fake<INotificationChannel>();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).ReturnsNextFromSequence(false, true);
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);

        await fanOut.SendAsync(Note(), CancellationToken.None);
        bool retry = await fanOut.SendAsync(Note(), CancellationToken.None);

        retry.Should().BeTrue();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldPropagate_WhenTheCallerCancels()
    {
        INotificationChannel cancelling = A.Fake<INotificationChannel>();
        A.CallTo(() => cancelling.SendAsync(A<Notification>._, A<CancellationToken>._)).Throws<OperationCanceledException>();
        FanOutNotificationChannel fanOut = new([Lane(cancelling), Lane(Transport())]);

        Func<Task> act = () => fanOut.SendAsync(Note(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAsync_ShouldForwardToEveryLane_AndReportClosed_WhenAllConfirm()
    {
        INotificationChannel pushover = Transport();
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);

        bool closed = await fanOut.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        closed.Should().BeTrue();
        A.CallTo(() => pushover.ResolveAsync("flatten:9001:ES", CancellationToken.None)).MustHaveHappenedOnceExactly();
        A.CallTo(() => discord.ResolveAsync("flatten:9001:ES", CancellationToken.None)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReportNotClosedButStillReachEveryLane_WhenOneCancelFails()
    {
        // A false asks the caller to retry (gh#300); the lane that already closed must not be skipped, and the
        // one that could not must not be masked by a sibling's success.
        INotificationChannel pushover = A.Fake<INotificationChannel>();
        A.CallTo(() => pushover.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(false);
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);

        bool closed = await fanOut.ResolveAsync("k", CancellationToken.None);

        closed.Should().BeFalse();
        A.CallTo(() => discord.ResolveAsync("k", CancellationToken.None)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldRearmEveryLane_SoARecurrenceIsReportedAgain()
    {
        INotificationChannel pushover = Transport();
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);
        await fanOut.SendAsync(Note(), CancellationToken.None);

        await fanOut.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        bool recurrence = await fanOut.SendAsync(Note(), CancellationToken.None);

        recurrence.Should().BeTrue();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ReleaseIncident_ShouldReleaseTheKeyInEveryLane_AndReportHeld()
    {
        // gh#1077: the queue's out-of-band release goes through this registry, and it must reach EVERY lane's
        // memory or one transport keeps suppressing the operator's next outage.
        INotificationChannel pushover = Transport();
        INotificationChannel discord = Transport();
        FanOutNotificationChannel fanOut = new([Lane(pushover), Lane(discord)]);
        await fanOut.SendAsync(Note(), CancellationToken.None);

        bool held = fanOut.ReleaseIncident("flatten:9001:ES");
        bool recurrence = await fanOut.SendAsync(Note(), CancellationToken.None);

        held.Should().BeTrue();
        recurrence.Should().BeTrue();
        A.CallTo(() => pushover.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => discord.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public void ReleaseIncident_ShouldReportNothingHeld_WhenNoLaneHoldsTheKey()
    {
        FanOutNotificationChannel fanOut = new([Lane(Transport()), Lane(Transport())]);

        fanOut.ReleaseIncident("never-reported").Should().BeFalse();
    }
}
