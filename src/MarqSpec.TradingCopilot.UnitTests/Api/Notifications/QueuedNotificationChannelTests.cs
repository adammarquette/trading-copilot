using System.Diagnostics;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Notifications;

/// <summary>
/// Keeping the notification send <b>off the flatten hot path</b> (gh#289, regression on gh#243).
/// </summary>
/// <remarks>
/// <para>
/// gh#243 claimed "sending is off the hot path" and was wrong: <c>AutoFlattenService.NotifyAsync</c> awaited the
/// send inline, so a slow channel added its full latency to a flatten pass — on the R-13 safety path, measured
/// against a close where CL and GC have roughly fifteen minutes of margin in total. gh#246 reproduced it: a
/// channel blocking 5 s made the pass take 5.15 s.
/// </para>
/// <para>
/// The fix makes non-blocking <b>structural</b> rather than a discipline each call site has to remember: the send
/// is enqueued and drained by a background pump. Two properties fall out that a bare fire-and-forget would not
/// have given — the drain is <b>single-threaded</b>, so dedup can no longer race itself, and it sees the
/// <i>real</i> delivery result, so a failed push still is not mistaken for one the operator received.
/// </para>
/// </remarks>
public class QueuedNotificationChannelTests
{
    private readonly INotificationChannel _inner = A.Fake<INotificationChannel>();

    public QueuedNotificationChannelTests()
    {
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
    }

    private QueuedNotificationChannel Channel() =>
        new(_inner, NullLogger<QueuedNotificationChannel>.Instance);

    private static Notification Note(string key = "flatten:9001:ES") =>
        new(NotificationSeverity.Page, "Auto-flatten escalated", "body", key);

    // --- The regression itself ---

    [Fact]
    public async Task SendAsync_ShouldReturnImmediately_WhenTheInnerChannelBlocks()
    {
        // THE guard for gh#289. A channel that hangs for 5 s must not hold the caller for 5 s -- the caller is a
        // flatten pass, and the hang happens precisely when a position is already failing to close.
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._))
            .ReturnsLazily(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); return true; });
        QueuedNotificationChannel channel = Channel();

        Stopwatch clock = Stopwatch.StartNew();
        await channel.SendAsync(Note(), CancellationToken.None);
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnImmediately_WhenTheInnerChannelBlocks()
    {
        // Resolve runs on the SUCCESS path of a flatten -- the common case, every session. Blocking here would
        // put channel latency on a pass that is working correctly.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); return true; });
        QueuedNotificationChannel channel = Channel();

        Stopwatch clock = Stopwatch.StartNew();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        clock.Stop();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // --- Enqueued still means delivered ---

    [Fact]
    public async Task DrainPendingAsync_ShouldDeliverWhatWasEnqueued()
    {
        QueuedNotificationChannel channel = Channel();
        await channel.SendAsync(Note(), CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldForwardResolves()
    {
        // Returns(true) is now load-bearing, not ceremony: after gh#300 a resolve reports whether the cancel
        // landed, and an unconfigured fake returns default(bool) == false -- which the pump correctly reads as
        // "the page is still nagging" and retries. Saying so keeps this test about forwarding.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    // --- gh#300: the pump owns the cancel-retry ---

    [Fact]
    public async Task DrainPendingAsync_ShouldRetryTheResolve_WhenTheCancelIsNotConfirmed()
    {
        // gh#300: an Emergency page nags until acknowledged, so a cancel that did not land leaves the operator
        // being woken for a position that is already flat. The pump is the "background best-effort cancel-retry,
        // off the hot path" the issue asked for -- retrying here costs the flatten nothing.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(false).Once()
            .Then.Returns(true);
        QueuedNotificationChannel channel = Channel();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldNotRetryTheResolve_WhenTheCancelIsConfirmed()
    {
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldBoundTheCancelRetries_WhenTheCancelNeverSucceeds()
    {
        // Unbounded retry would turn a permanently-rejected receipt into a spin that starves every other
        // delivery on this single-reader pump -- alerting breaking trading is the wound ADR-0019 rules out.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(false);
        QueuedNotificationChannel channel = Channel();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._))
            .MustHaveHappened(QueuedNotificationChannel.MaxResolveAttempts, Times.Exactly);
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldNotRetryASend_WhenDeliveryFails()
    {
        // Only the resolve is retried. A failed SEND is already handled below by DedupingNotificationChannel,
        // which declines to record the incident so the next escalation pass re-sends it naturally -- retrying
        // here as well would double-page.
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(false);
        QueuedNotificationChannel channel = Channel();
        await channel.SendAsync(Note(), CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldPreserveOrder_BetweenASendAndItsResolve()
    {
        // A resolve that overtook its own send would cancel a page that had not been raised yet, and then the
        // send would raise one nothing will ever clear.
        List<string> order = [];
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._))
            .ReturnsLazily(() => { order.Add("send"); return Task.FromResult(true); });
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => { order.Add("resolve"); return Task.FromResult(true); });
        QueuedNotificationChannel channel = Channel();

        await channel.SendAsync(Note(), CancellationToken.None);
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        order.Should().Equal("send", "resolve");
    }

    // --- Faults stay contained ---

    [Fact]
    public async Task DrainPendingAsync_ShouldKeepDraining_WhenOneDeliveryThrows()
    {
        // A detached send that throws must not take the pump down with it, or the FIRST failure would silence
        // every notification after it.
        A.CallTo(() => _inner.SendAsync(A<Notification>.That.Matches(n => n.DedupKey == "bad"), A<CancellationToken>._))
            .Throws(new InvalidOperationException("channel exploded"));
        QueuedNotificationChannel channel = Channel();

        await channel.SendAsync(Note("bad"), CancellationToken.None);
        await channel.SendAsync(Note("good"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>.That.Matches(n => n.DedupKey == "good"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldNotThrow_WhenTheInnerChannelThrows()
    {
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("boom"));
        QueuedNotificationChannel channel = Channel();

        Func<Task> act = async () =>
        {
            await channel.SendAsync(Note(), CancellationToken.None);
            await channel.DrainPendingAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    // --- Shutdown ---

    [Fact]
    public async Task DrainPendingAsync_ShouldFlushEverythingQueued_SoShutdownDoesNotDropAPage()
    {
        // A page queued as the host stops is the one most worth delivering: it is raised when something has
        // already gone wrong.
        QueuedNotificationChannel channel = Channel();
        await channel.SendAsync(Note("a"), CancellationToken.None);
        await channel.SendAsync(Note("b"), CancellationToken.None);
        await channel.SendAsync(Note("c"), CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task SendAsync_ShouldNotUseTheCallersToken_ForDelivery()
    {
        // The caller's token is the FLATTEN's. If delivery inherited it, a pass that finished (or a host that
        // stopped) would cancel the page describing why it failed.
        using CancellationTokenSource callerDone = new();
        QueuedNotificationChannel channel = Channel();

        await channel.SendAsync(Note(), callerDone.Token);
        await callerDone.CancelAsync();
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(
                A<Notification>._, A<CancellationToken>.That.Matches(t => !t.IsCancellationRequested)))
            .MustHaveHappenedOnceExactly();
    }
}
