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

    /// <summary>Enqueues exactly the page budget, asserting each one was accepted — nothing drains it.</summary>
    private static async Task FillPageBudgetAsync(QueuedNotificationChannel channel)
    {
        for (int i = 0; i < QueuedNotificationChannel.PageCapacity; i++)
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
