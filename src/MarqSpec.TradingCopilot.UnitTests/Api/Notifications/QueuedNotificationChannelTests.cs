using System.Diagnostics;
using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Notifications;
using MarqSpec.TradingCopilot.Api.Observability;
using MarqSpec.TradingCopilot.Domain.Notifications;
using MarqSpec.TradingCopilot.Domain.Observability;
using Microsoft.Extensions.Logging;

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
    private readonly IIncidentKeyRegistry _incidents = A.Fake<IIncidentKeyRegistry>();
    private readonly IExecutionMetrics _metrics = A.Fake<IExecutionMetrics>();
    private readonly ILogger<QueuedNotificationChannel> _logger = A.Fake<ILogger<QueuedNotificationChannel>>();

    public QueuedNotificationChannelTests()
    {
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
    }

    private QueuedNotificationChannel Channel() => new(_inner, _incidents, _metrics, _logger);

    private static Notification Note(string key = "flatten:9001:ES") =>
        new(NotificationSeverity.Page, "Auto-flatten escalated", "body", key);

    /// <summary>
    /// Fills the page budget, asserting each one was accepted — nothing drains it. <paramref name="alreadyQueued"/>
    /// discounts items a test put in before calling, because the cap is on total queue depth.
    /// </summary>
    private static async Task FillPageBudgetAsync(QueuedNotificationChannel channel, int alreadyQueued = 0)
    {
        for (int i = 0; i < QueuedNotificationChannel.PageCapacity - alreadyQueued; i++)
        {
            (await channel.SendAsync(Note($"fill:{i}"), CancellationToken.None))
                .Should().BeTrue($"page {i} is inside the budget of {QueuedNotificationChannel.PageCapacity}");
        }
    }

    /// <summary>Enqueues exactly the reserve, asserting each one was accepted.</summary>
    private static async Task FillResolveHeadroomAsync(QueuedNotificationChannel channel)
    {
        for (int i = 0; i < QueuedNotificationChannel.ResolveHeadroom; i++)
        {
            (await channel.ResolveAsync($"fill-resolve:{i}", CancellationToken.None))
                .Should().BeTrue($"resolve {i} is inside the reserve of {QueuedNotificationChannel.ResolveHeadroom}");
        }
    }

    private void MustHaveLogged(LogLevel level) =>
        A.CallTo(_logger).Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == level)
            .MustHaveHappened();

    private void MustNotHaveLogged(LogLevel level) =>
        A.CallTo(_logger).Where(call => call.Method.Name == "Log" && call.GetArgument<LogLevel>(0) == level)
            .MustNotHaveHappened();

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

    // --- gh#1077: a full queue REFUSES; it never drops ---
    //
    // The class was written with BoundedChannelFullMode.DropWrite and a comment promising "a drop is logged".
    // Under EVERY Drop* mode TryWrite discards the item and returns TRUE, so both of its "queue is full" branches
    // were unreachable: nothing was logged, and every caller -- the auto-flatten, the watchdog, the kill switch --
    // was told the page landed. There was no test here that filled the queue, which is why a dead branch could sit
    // in the R-13 alerting path unnoticed. These fixtures fill it, and they assert on ABSENCE where absence is the
    // property: a channel that silently drops is precisely the case a survivor-style assertion cannot catch.

    [Fact]
    public async Task SendAsync_ShouldReturnFalse_WhenThePageBudgetIsFull()
    {
        // The headline defect. Nothing drains this channel, so the budget fills and stays full -- the shape a
        // wedged Pushover produces while the escalation re-emits every 15 s.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);

        bool accepted = await channel.SendAsync(Note("overflow"), CancellationToken.None);

        accepted.Should().BeFalse(
            "a page the queue cannot take must be REFUSED, not accepted and discarded -- the outbox above only "
            + "keeps a row owed when it is told the chain did not take it (gh#1077)");
    }

    [Fact]
    public async Task SendAsync_ShouldNotDeliverTheRefusedPage_WhenThePageBudgetIsFull()
    {
        // The refusal has to be HONEST: `false` while the item was in fact queued would make the outbox re-offer a
        // page that then goes out twice. Asserted as an absence, because "it was not queued" has no survivor.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await channel.SendAsync(Note("overflow"), CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "overflow"), A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task SendAsync_ShouldKeepEveryAcceptedPage_WhenAFurtherPageIsRefused()
    {
        // Why refusing beats DropOldest, asserted rather than argued. During an incident the FIRST page is the one
        // that matters and the later ones are repeats dedup would collapse; a mode that makes room by discarding
        // the head would lose exactly the page worth keeping, and would still report success while doing it.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await channel.SendAsync(Note("overflow"), CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "fill:0"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _inner.SendAsync(A<Notification>._, A<CancellationToken>._))
            .MustHaveHappened(QueuedNotificationChannel.PageCapacity, Times.Exactly);
    }

    [Fact]
    public async Task SendAsync_ShouldLogAtError_WhenThePageBudgetIsFull()
    {
        // The LEVEL is the assertion. appsettings.json sets Logging:LogLevel:Default to Information, so a
        // LogDebug/LogTrace here would never be written in production -- a "logged" refusal nobody can read is the
        // same silence, one level down.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);

        await channel.SendAsync(Note("overflow"), CancellationToken.None);

        MustHaveLogged(LogLevel.Error);
    }

    [Fact]
    public async Task SendAsync_ShouldMeterTheRefusal_WhenThePageBudgetIsFull()
    {
        // A log line is visible to an engineer who goes looking; the OPERATOR is the one not being told. Metering
        // hands the fact to Layer 2, whose job under ADR-0019 is to cover what Layer 1 cannot self-report -- and a
        // refusing queue is exactly a Layer-1 blind spot.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);

        await channel.SendAsync(Note("overflow"), CancellationToken.None);

        A.CallTo(() => _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedPage))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SendAsync_ShouldNotRefuse_WhileThePageBudgetHasRoom()
    {
        // The other half of the guard: the cap must not fire early. A channel that refused below its budget would
        // turn a busy-but-healthy incident into lost pages, and the fixtures above would still be green.
        QueuedNotificationChannel channel = Channel();

        await FillPageBudgetAsync(channel);

        MustNotHaveLogged(LogLevel.Error);
        A.CallTo(() => _metrics.RecordNotificationRefused(A<string>._)).MustNotHaveHappened();
    }

    // --- gh#1077: the resolve has its own reserve, because its loss is the unbounded one ---

    [Fact]
    public async Task ResolveAsync_ShouldStillBeAccepted_WhenPagesHaveFilledTheirBudget()
    {
        // What fills this queue is PAGES -- the escalation re-emitting against a transport that is not draining --
        // so without a reserve the resolve is precisely the item crowded out, and a lost resolve is the failure
        // that compounds: DedupingNotificationChannel releases a key only through ResolveAsync.
        //
        // Returns(true) is load-bearing here for the same reason it is in DrainPendingAsync_ShouldForwardResolves:
        // an unconfigured fake returns false, which the pump correctly reads as "the cancel did not land" and
        // retries -- turning a forwarding assertion into a retry-count assertion.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);

        bool accepted = await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        accepted.Should().BeTrue("the reserve exists so a queue full of pages cannot crowd out a resolve");
        await channel.DrainPendingAsync(CancellationToken.None);
        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnFalse_WhenEvenTheReserveIsFull()
    {
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);

        bool accepted = await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        accepted.Should().BeFalse(
            "INotificationChannel says false means the cancel could not be confirmed, which asks the caller to "
            + "try again -- being told true would leave an Emergency page nagging with nobody retrying it");
    }

    [Fact]
    public async Task ResolveAsync_ShouldReleaseTheDedupKeyOutOfBand_WhenEvenTheReserveIsFull()
    {
        // THE COMPOUNDING FAILURE, closed by construction. The cancel is recoverable -- a caller sees false and
        // retries. The RELEASE is recoverable by nobody: there is no outbox row for a resolve, and
        // TriggerEvaluationService's staleness recovery resolves exactly once per outage and never returns. Left
        // held, the key suppresses every later incident on it for the life of the process (gh#1045, gh#1051).
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _incidents.ReleaseIncident("flatten:9001:ES")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotReleaseTheDedupKeyOutOfBand_WhenTheResolveIsEnqueued()
    {
        // The fallback is a fallback. Releasing on the ORDINARY path would move the re-arm off the single-threaded
        // pump and back onto the caller's thread, letting a concurrent escalation on the same key slip past the
        // suppression it is meant to meet -- the race the queue sits below dedup to remove (gh#289).
        QueuedNotificationChannel channel = Channel();

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _incidents.ReleaseIncident(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ResolveAsync_ShouldLogAtCritical_WhenEvenTheReserveIsFull()
    {
        // Louder than a refused page, and deliberately: a refused page is still owed by the outbox, a refused
        // resolve is owed by nothing.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        MustHaveLogged(LogLevel.Critical);
    }

    [Fact]
    public async Task ResolveAsync_ShouldMeterTheRefusal_WhenEvenTheReserveIsFull()
    {
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _metrics.RecordNotificationRefused(ExecutionMetrics.NotificationRefusedResolve))
            .MustHaveHappenedOnceExactly();
    }

    // --- gh#1077 round-2 review: the release alone did not hold, and the reserve was sized on a false rate ---

    [Fact]
    public async Task DrainPendingAsync_ShouldReleaseTheKeyAgain_WhenAPageWasQueuedBehindARefusedResolve()
    {
        // THE ROUND-2 BLOCKING FINDING. Releasing the key at the moment of refusal is not enough: dedup ARMS a key
        // on a successful send, so a page still sitting in the queue re-arms the key the refusal just released --
        // and the resolve that would have cleared it is gone. This is production's shape, not a contrived one: the
        // relay enqueues the page, the transport wedges, and the producer's resolve arrives while it is stuck.
        QueuedNotificationChannel channel = Channel();

        // The page for our incident goes in FIRST, and stays queued.
        (await channel.SendAsync(Note("outage"), CancellationToken.None)).Should().BeTrue();

        // Then the queue fills and the resolve for that same incident is refused.
        await FillPageBudgetAsync(channel, alreadyQueued: 1);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        await channel.DrainPendingAsync(CancellationToken.None);

        // ONCE, and by the covering page's delivery rather than by the refusal (round-4 review). While a page for
        // the key is queued the refusal must NOT release: the wedge refuses again every 15 s, and each of those
        // releases would un-suppress the next queued page.
        A.CallTo(() => _incidents.ReleaseIncident("outage")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldNotReleaseTheKey_WhenThePageWasQueuedAfterTheRefusedResolve()
    {
        // The other side of the same rule, and the reason the marker is ordinal-guarded rather than a bare flag. A
        // page enqueued AFTER the refusal is a NEW incident: it must keep its suppression, or the escalation
        // re-emitting every 15 s pages every pass -- the noise ADR-0019 §4 forbids, reached by way of a guard
        // against silence.
        QueuedNotificationChannel channel = Channel();
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        await channel.DrainPendingAsync(CancellationToken.None);   // make room again
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        // Only the release at the refusal itself; the later page is a new incident and keeps its key.
        A.CallTo(() => _incidents.ReleaseIncident("outage")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotEnqueueARepeat_WhileAResolveForTheSameKeyIsAlreadyQueued()
    {
        // Round-2 finding: the reserve was documented as sized against OPEN INCIDENTS, but AutoFlattenService
        // resolves every configured instrument on every 15 s pass whether or not anything was ever paged -- so
        // sized against the real rate a 64-slot reserve is four minutes of a wedged transport, not a residual
        // case. Collapsing repeats is what makes the documented bound true.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();

        for (int pass = 0; pass < 50; pass++)
        {
            (await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None)).Should().BeTrue(
                "a repeat is covered by the resolve already queued -- still accepted for delivery");
        }

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldEnqueueAgain_AfterTheQueuedResolveHasBeenDelivered()
    {
        // The collapse must not outlive the queued resolve, or the SECOND incident on that key is never closed --
        // silence, reached by way of an optimisation.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ResolveAsync_ShouldEnqueueAgain_WhenAPageWasEnqueuedAfterTheQueuedResolve()
    {
        // Ordering, which is what makes the collapse safe. A page enqueued after a queued resolve is a NEW
        // incident that the queued resolve -- which sits ahead of it -- cannot close, so the next resolve has to
        // be a fresh item behind that page rather than folded into one in front of it.
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.SendAsync(Note(), CancellationToken.None);
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldAbandonTheCancelRetry_WhenTheQueueRefillsWhileItIsDelivering()
    {
        // The THIRD refusal site, which had no test. It is only reachable when producers refill the slot the pump
        // just freed, between its read and its retry write -- so the fake does exactly that, from inside the
        // transport call, which is precisely the production interleaving (the pump is the only reader; producers
        // write concurrently). Without the guard the retry would spin on a queue it cannot re-enter.
        QueuedNotificationChannel channel = Channel();
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._))
            .Invokes(() =>
            {
                // Refill to the HARD bound with resolves only, each on its own key. Resolves carry no soft cap, so
                // exactly capacity of them fit and NONE is itself refused -- which keeps the Error assertion below
                // about the cancel-retry rather than about refusals the fixture manufactured.
                for (int i = 0; i < QueuedNotificationChannel.PageCapacity + QueuedNotificationChannel.ResolveHeadroom; i++)
                {
                    channel.ResolveAsync($"refill-resolve:{i}", CancellationToken.None).GetAwaiter().GetResult()
                        .Should().BeTrue("the refill must fit exactly, so nothing but the cancel-retry is refused");
                }
            })
            .Returns(false);

        await channel.DrainPendingAsync(CancellationToken.None);

        // ONCE, not MaxResolveAttempts: the retry could not be re-queued, so it is given up loudly rather than
        // spun on.
        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        MustHaveLogged(LogLevel.Error);
    }

    [Fact]
    public async Task ResolveAsync_ShouldEnqueueAgain_AfterItsCancelRetryCouldNotBeReQueued()
    {
        // Found by mutation: removing the give-up `return` left the collapse marker set for a retry that was
        // REFUSED, so the next resolve for that key collapsed into an item that is not in the queue at all. The
        // incident would then never be closed and the key never released -- silence, reached by way of the
        // optimisation that exists to keep the reserve free. Every other case here stayed green through that
        // mutation, which is why this one exists.
        bool refilled = false;
        A.CallTo(() => _inner.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        QueuedNotificationChannel channel = Channel();

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (refilled)
                {
                    return true;
                }

                // First delivery only: the cancel does not land AND producers refill the slot the pump just
                // freed, so its retry cannot be re-queued.
                refilled = true;
                for (int i = 0; i < QueuedNotificationChannel.PageCapacity + QueuedNotificationChannel.ResolveHeadroom; i++)
                {
                    channel.ResolveAsync($"refill-resolve:{i}", CancellationToken.None).GetAwaiter().GetResult()
                        .Should().BeTrue("the refill must fit exactly");
                }

                return false;
            });

        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        // A later pass resolves the same incident again. It MUST be enqueued.
        await channel.ResolveAsync("flatten:9001:ES", CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _inner.ResolveAsync("flatten:9001:ES", A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    // --- gh#1077 round-3 review: the collapse race, and one incident staying one push ---

    [Theory]
    // A resolve queued behind every page for its key: collapsing is safe.
    [InlineData(10L, null, true)]
    [InlineData(10L, 4L, true)]
    // A page queued AFTER the resolve: the queued resolve sits in front of it and cannot close what it reports.
    [InlineData(4L, 10L, false)]
    // THE RACED ORDERING, which no single-threaded fixture can produce: the resolve wrote first and took the
    // lower ordinal, but its bookkeeping landed after the page's. Under the old presence-based rule the marker
    // was simply "set" and this collapsed; comparing ordinals makes the answer independent of store order.
    [InlineData(9L, 10L, false)]
    [InlineData(10L, 10L, false)]
    public void CollapsesIntoQueuedResolve_ShouldOnlyCollapse_WhenTheResolveIsBehindEveryPageForThatKey(
        long queuedResolveOrdinal, long? newestPageOrdinal, bool expected)
    {
        QueuedNotificationChannel.CollapsesIntoQueuedResolve(queuedResolveOrdinal, newestPageOrdinal)
            .Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotLoseAResolve_WhenAProducerAndTheRelayRaceOnTheSameKey()
    {
        // THE ROUND-3 BLOCKING FINDING, as a fixture that can actually fail on it. Every other collapse test here
        // is single-threaded, which is exactly why the race got through: the bad state needs a resolve's marker to
        // land AFTER a page for the same key has already cleared it, leaving a resolve queued AHEAD of a page with
        // the collapse still armed. In production the two threads are AutoFlattenHost and the outbox relay host,
        // contending on the same flatten key as a matter of routine.
        //
        // A stress fixture, deliberately: it cannot fail unless the invariant is broken (the rule only ever errs
        // toward enqueueing), and it exercises the interleaving rather than asserting it exists. The deterministic
        // pin for the rule itself is the theory above.
        INotificationChannel transport = A.Fake<INotificationChannel>();
        A.CallTo(() => transport.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => transport.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        DedupingNotificationChannel deduping = new(transport, A.Fake<ILogger<DedupingNotificationChannel>>());
        QueuedNotificationChannel channel = new(deduping, deduping, _metrics, _logger);

        for (int round = 0; round < 300; round++)
        {
            string key = $"race:{round}";

            // The producer resolves while the relay sends, on the same key. Either order may reach the queue.
            await Task.WhenAll(
                Task.Run(() => channel.ResolveAsync(key, CancellationToken.None)),
                Task.Run(() => channel.SendAsync(Note(key), CancellationToken.None)));

            // The next pass resolves the same key. If a page is queued after the resolve, this MUST be enqueued.
            await channel.ResolveAsync(key, CancellationToken.None);
            await channel.DrainPendingAsync(CancellationToken.None);

            // The key must not be left armed: a later, independent incident on it has to reach the operator.
            await channel.SendAsync(Note(key), CancellationToken.None);
            await channel.DrainPendingAsync(CancellationToken.None);

            A.CallTo(() => transport.SendAsync(
                    A<Notification>.That.Matches(n => n.DedupKey == key), A<CancellationToken>._))
                .MustHaveHappenedTwiceExactly();
        }
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldReleaseTheKeyOnce_WhenSeveralPagesWereQueuedBehindARefusedResolve()
    {
        // Round-3 finding 2. Releasing after EVERY covered page let each next page through the dedup it had just
        // cleared, so one incident became one Emergency push per queued page -- dozens during the wedge this
        // change exists for, which ADR-0019 §4 calls strictly worse than no pager. The release is now bound to the
        // LAST page the refusal covered. The previous fixture queued exactly one page, which is why this was
        // invisible.
        QueuedNotificationChannel channel = Channel();
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.SendAsync(Note("outage"), CancellationToken.None);

        await FillPageBudgetAsync(channel, alreadyQueued: 3);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        await channel.DrainPendingAsync(CancellationToken.None);

        // ONCE, after the LAST covered page -- not once per page, and not at the refusal as well.
        A.CallTo(() => _incidents.ReleaseIncident("outage")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ABacklogBehindARefusedResolve_ShouldStillBeOneEmergencyPush_AndStillLeaveTheKeyReleased()
    {
        // The same property where it is actually felt, over the REAL dedup decorator: the operator gets ONE push
        // for one incident however deep the backlog, AND the key ends released so the next incident is reported.
        // Both halves matter -- bounding the pushes by leaving the key armed would trade the flood back for the
        // silence this card exists to remove.
        INotificationChannel transport = A.Fake<INotificationChannel>();
        A.CallTo(() => transport.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => transport.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        DedupingNotificationChannel deduping = new(transport, A.Fake<ILogger<DedupingNotificationChannel>>());
        QueuedNotificationChannel channel = new(deduping, deduping, _metrics, _logger);

        for (int i = 0; i < 4; i++)
        {
            await channel.SendAsync(Note("outage"), CancellationToken.None);
        }

        await FillPageBudgetAsync(channel, alreadyQueued: 4);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => transport.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // ...and the key is released, so the NEXT independent incident on it is still reported.
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => transport.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ABacklogBehindRepeatedRefusals_ShouldStillBeOneEmergencyPush()
    {
        // THE ROUND-4 BLOCKING FINDING, and the fixture gap that hid it: the storm test refuses ONCE and then
        // drains uninterrupted, which is not what a wedge does. A wedge refuses a resolve for the same key on
        // EVERY 15 s flatten pass while the backlog drains at ~6/min, so refusals land BETWEEN deliveries -- and
        // an unconditional release on each one un-suppressed the next queued page. One push per refusal, which
        // over a multi-minute wedge is round-3's flood at a lower rate.
        //
        // The refusal is injected from inside the transport call, which is exactly where a real one lands: the
        // pump is mid-delivery, the queue is still full, and the next producer pass resolves.
        INotificationChannel transport = A.Fake<INotificationChannel>();
        A.CallTo(() => transport.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => transport.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        DedupingNotificationChannel deduping = new(transport, A.Fake<ILogger<DedupingNotificationChannel>>());

        // The hook fires AFTER the dedup layer has armed the key, which is where a real refusal lands: BETWEEN
        // two deliveries, not inside one. Injecting from the transport fake instead runs BEFORE dedup arms, so
        // the release finds nothing held and the scenario evaporates -- the first cut of this fixture did exactly
        // that and stayed green under the very defect it was written for.
        QueuedNotificationChannel channel = null!;
        bool injected = false;
        AfterSendHook hook = new(deduping, () =>
        {
            if (injected)
            {
                return;
            }

            injected = true;

            // Still wedged: refill the slots the pump has freed, then take the next flatten pass's resolve.
            int i = 0;
            while (channel.ResolveAsync($"wedge:{i++}", CancellationToken.None).GetAwaiter().GetResult() && i < 1000)
            {
            }

            channel.ResolveAsync("outage", CancellationToken.None).GetAwaiter().GetResult()
                .Should().BeFalse("the wedge is still on, so this pass's resolve is refused too");
        });

        channel = new QueuedNotificationChannel(hook, deduping, _metrics, _logger);

        for (int i = 0; i < 4; i++)
        {
            await channel.SendAsync(Note("outage"), CancellationToken.None);
        }

        await FillPageBudgetAsync(channel, alreadyQueued: 4);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => transport.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Runs <c>afterSend</c> once the inner chain has finished a send — so a fixture can act <b>between</b> two
    /// deliveries, after the dedup layer has armed the key, which a transport-level hook cannot.
    /// </summary>
    private sealed class AfterSendHook : INotificationChannel
    {
        private readonly INotificationChannel _inner;
        private readonly Action _afterSend;

        public AfterSendHook(INotificationChannel inner, Action afterSend)
        {
            _inner = inner;
            _afterSend = afterSend;
        }

        public async Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            bool sent = await _inner.SendAsync(notification, cancellationToken);
            _afterSend();
            return sent;
        }

        public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken) =>
            _inner.ResolveAsync(dedupKey, cancellationToken);
    }

    [Fact]
    public async Task DrainPendingAsync_ShouldStillReleaseTheKey_WhenTheCoveringPagesSendThrows()
    {
        // The release's owner is the covering page's delivery, so that delivery must hand the key back even when
        // it faults -- otherwise a throwing send strands the marker while an earlier backlog page has already
        // armed the key, and it is held for the life of the process by way of an exception path. That is this
        // card's own failure family reached from a direction nothing else here covers.
        QueuedNotificationChannel channel = Channel();
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await FillPageBudgetAsync(channel, alreadyQueued: 1);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        A.CallTo(() => _inner.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .Throws(new InvalidOperationException("transport exploded"));

        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => _incidents.ReleaseIncident("outage")).MustHaveHappenedOnceExactly();
    }

    // --- gh#1077: the invariant the whole card is about, over the REAL dedup decorator ---

    [Fact]
    public async Task AFullQueue_ShouldStillReportTheNextIncident_WhenTheResolveForTheLastOneWasRefused()
    {
        // "One notification per OUTAGE, not one per process lifetime" (ADR-0019). Composed over a real
        // DedupingNotificationChannel rather than a fake, because the property is what the two classes do
        // together: the queue is the only thing between a producer and the key, and a resolve it cannot carry is
        // the one way a key is never released.
        INotificationChannel transport = A.Fake<INotificationChannel>();
        A.CallTo(() => transport.SendAsync(A<Notification>._, A<CancellationToken>._)).Returns(true);
        A.CallTo(() => transport.ResolveAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        DedupingNotificationChannel deduping = new(transport, A.Fake<ILogger<DedupingNotificationChannel>>());
        QueuedNotificationChannel channel = new(deduping, deduping, _metrics, _logger);

        // The first outage is reported, so the key is now held.
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        // CONTROL: prove the key really IS held, or everything below passes vacuously on a dedup layer that never
        // armed. A repeat while the incident is open must be suppressed.
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);
        A.CallTo(() => transport.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();

        // The transport wedges: pages fill the budget, resolves fill the reserve, and the resolve that closes OUR
        // incident is the one that cannot get in.
        await FillPageBudgetAsync(channel);
        await FillResolveHeadroomAsync(channel);
        (await channel.ResolveAsync("outage", CancellationToken.None)).Should().BeFalse();

        // The backlog eventually drains, and a LATER, independent outage occurs on the same key.
        await channel.DrainPendingAsync(CancellationToken.None);
        await channel.SendAsync(Note("outage"), CancellationToken.None);
        await channel.DrainPendingAsync(CancellationToken.None);

        A.CallTo(() => transport.SendAsync(
                A<Notification>.That.Matches(n => n.DedupKey == "outage"), A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
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
