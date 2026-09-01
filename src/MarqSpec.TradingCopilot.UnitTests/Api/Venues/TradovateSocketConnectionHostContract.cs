using System.Diagnostics;
using FakeItEasy;
using MarqSpec.Client.Tradovate.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Venues;

/// <summary>
/// The behaviour <b>every</b> Tradovate socket-lifecycle host owes, inherited by each host's own suite (gh#1054).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists.</b> The market-data host and the trading host were hand-maintained copies of one poll
/// loop, and within a week they disagreed about whether a successful connect resets the backoff — a divergence that
/// leaves a socket <c>Connected</c>, and therefore healthy-looking to every other reader in the process, while it
/// delivers nothing for up to a minute per retry. Nothing in CI could have caught it: both copies were internally
/// consistent and both suites were green, because the rule was pinned by a test <i>one of them had</i>. Every test
/// in this class is inherited by both suites, so a mutation to the shared loop reds it twice.
/// </para>
/// <para>
/// <b>What belongs here and what does not.</b> Here: the seven invariants gh#1054 names — backoff growth and its
/// reset, the <c>Connecting</c>/<c>Reconnecting</c> wait, the unrecognised-state default, containment, the
/// <see cref="OperationCanceledException"/> retry condition, lazy resolve with the stand-down triad, and clean exit.
/// Not here: the <b>post-connect obligation</b>, which differs in kind (a per-key replay that must survive partial
/// failure versus a single <c>user/syncrequest</c>) and the trading socket's <c>ConnectionStatusChanged</c> re-arm,
/// which market data does not need because its replay failure propagates and parks that socket in
/// <c>Disconnected</c>. Those stay in each host's own suite, as differences on purpose rather than by accident.
/// </para>
/// <para>
/// <b>The seams.</b> A subclass says which socket it drives (<see cref="ArrangeSocketState"/>,
/// <see cref="ArrangeConnect"/>), how its post-connect obligation is made to succeed or fail
/// (<see cref="ArrangePostConnectWorkAsync"/>), and how its collaborators are registered. Everything else — the
/// pass counter, the bounded waits, the assertions — is shared, so a host cannot quietly opt out of an invariant by
/// not writing its copy of the test.
/// </para>
/// <para>
/// <b>Every wait is time-bound.</b> A bare <see cref="TaskCompletionSource"/> await that never completes hangs the
/// whole xunit run and reads as slow CI rather than as a red test. Every test that proves a <b>negative</b> first
/// waits on <see cref="PassesObserved"/>, so the assertion rests on passes that really ran rather than on a sleep
/// the host might have spent doing nothing at all.
/// </para>
/// <para>
/// <b>One invariant is deliberately not here.</b> "A pass that owes nothing resets the backoff" is only observable
/// through a <i>later</i> pass that owes something, and only the trading socket can be pushed back into owing one
/// (its <c>ConnectionStatusChanged</c> re-arm). Market data's obligation, once settled, is re-armed only by a
/// host-driven connect — which resets the backoff anyway — so the two cannot be distinguished from this side. It is
/// pinned in the trading suite instead, and the code it pins is shared, so the mutation still dies.
/// </para>
/// </remarks>
public abstract class TradovateSocketConnectionHostContract
{
    /// <summary>The bound on every wait in this class — long enough never to fire on a healthy pass.</summary>
    protected static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The venue client both hosts drive. The subclass points the seams below at its own socket.</summary>
    protected ITradovateWebSocketClient Client { get; } = A.Fake<ITradovateWebSocketClient>();

    private readonly TaskCompletionSource _witness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stateReads;
    private int _witnessAt = int.MaxValue;

    /// <summary>How many loop passes the host has begun — one per read of the socket's state.</summary>
    protected int PassesSoFar => Volatile.Read(ref _stateReads);

    // ---------------------------------------------------------------------------------------------------------
    // Seams. Everything a subclass has to say about WHICH socket it drives and WHAT it owes after a connect.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Builds the host under test with a test cadence, over the given provider.</summary>
    protected abstract BackgroundService CreateHost(
        IServiceProvider services, TimeSpan pollInterval, TimeSpan maxBackoff);

    /// <summary>Registers <see cref="Client"/> and every collaborator the host requires.</summary>
    protected abstract void Register(ServiceCollection services);

    /// <summary>Registers <see cref="Client"/> and deliberately <b>omits</b> a required collaborator.</summary>
    protected abstract void RegisterWithoutARequiredCollaborator(ServiceCollection services);

    /// <summary>Points the fake's socket-state property at <paramref name="read"/>.</summary>
    protected abstract void ArrangeSocketState(Func<ClientModels.ConnectionState> read);

    /// <summary>Points the fake's connect call at <paramref name="behaviour"/>; throwing means the connect failed.</summary>
    protected abstract void ArrangeConnect(Func<Task> behaviour);

    /// <summary>
    /// Arranges the host's post-connect obligation so that exactly one unit of it runs per pass and is driven by
    /// <paramref name="behaviour"/> — returning means the obligation was met, throwing means it is still owed.
    /// </summary>
    protected abstract Task ArrangePostConnectWorkAsync(Func<Task> behaviour);

    /// <summary>Asserts the host never drove a connect.</summary>
    protected abstract void AssertNeverConnected();

    /// <summary>Asserts the host never attempted its post-connect obligation.</summary>
    protected abstract void AssertNeverDidPostConnectWork();

    // ---------------------------------------------------------------------------------------------------------
    // Shared harness.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A provider with everything the host needs.</summary>
    protected IServiceProvider Registered()
    {
        ServiceCollection services = new();
        Register(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Builds the host over <see cref="Registered"/> at the default test cadence.</summary>
    protected BackgroundService Host(IServiceProvider? services = null) =>
        CreateHost(
            services ?? Registered(),
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxBackoff: TimeSpan.FromMilliseconds(2));

    // Each state read is one loop pass, so counting them is the host's own heartbeat: it lets a "did not happen"
    // assertion say "across N real passes" rather than "within some wall-clock window", and it lets a test act at an
    // exact point in the host's cycle instead of racing it with a sleep. The witness is signalled in a `finally` so
    // that a state getter which THROWS still counts its pass -- the containment test depends on that.
    /// <summary>Drives the socket's reported state from <paramref name="read"/>, given the 1-based pass number.</summary>
    protected void SocketReadsAs(Func<int, ClientModels.ConnectionState> read) =>
        ArrangeSocketState(() =>
        {
            int pass = Interlocked.Increment(ref _stateReads);
            try
            {
                return read(pass);
            }
            finally
            {
                if (pass >= Volatile.Read(ref _witnessAt))
                {
                    _witness.TrySetResult();
                }
            }
        });

    /// <summary>Reports each state in turn, holding the last one for every pass after.</summary>
    protected void SocketIs(params ClientModels.ConnectionState[] states) =>
        SocketReadsAs(pass => states[Math.Min(pass - 1, states.Length - 1)]);

    /// <summary>Completes once the host has begun <paramref name="passes"/> passes; false means it timed out.</summary>
    protected Task<bool> PassesObserved(int passes)
    {
        Volatile.Write(ref _witnessAt, passes);
        return Signalled(_witness);
    }

    /// <summary>Awaits a signal under <see cref="Timeout"/> rather than forever.</summary>
    protected static async Task<bool> Signalled(TaskCompletionSource signal) =>
        await Task.WhenAny(signal.Task, Task.Delay(Timeout)) == signal.Task;

    // BackgroundService.StopAsync awaits ExecuteTask against Task.Delay(Timeout.Infinite, token), so stopping with
    // CancellationToken.None makes every teardown an UNBOUNDED wait: a loop that ignored its stopping token would
    // hang the whole xunit run rather than fail, which reads as slow CI instead of as a defect.
    //
    // It asserts IsFaulted as well as IsCompleted (gh#1055), because IsCompleted is TRUE for a faulted task -- so
    // on its own it proves the loop stopped, not that it stopped cleanly. Routing every test through this helper
    // makes all of them detectors for this class's central claim: nothing escapes ExecuteAsync, because the default
    // BackgroundServiceExceptionBehavior.StopHost would take the auto-flatten watchdog and the kill switch with it.
    /// <summary>Stops the host under <see cref="Timeout"/> and asserts it stopped, and stopped cleanly.</summary>
    protected static async Task StopAsync(BackgroundService host)
    {
        using CancellationTokenSource timeout = new(Timeout);
        await host.StopAsync(timeout.Token);
        host.ExecuteTask!.IsCompleted.Should().BeTrue("the host must stop when its stopping token is signalled");
        host.ExecuteTask!.IsFaulted.Should()
            .BeFalse("a faulted BackgroundService stops the whole application under the default StopHost behaviour");
    }

    // For the stand-down tests: awaiting ExecuteTask bare would hang forever against the exact bug each of them
    // names -- a host that failed to stand down and entered the poll loop instead.
    /// <summary>True when <paramref name="run"/> ended within <see cref="Timeout"/>; a fault is rethrown.</summary>
    protected static async Task<bool> RanToCompletion(Task run)
    {
        if (await Task.WhenAny(run, Task.Delay(Timeout)) != run)
        {
            return false;
        }

        await run; // a fault surfaces as this test's failure rather than as a silent false
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1 · Backoff growth, and its reset on a successful connect.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldResetTheBackoff_WhenAConnectSucceeds_SoTheFirstObligationIsNotChargedTheOutage()
    {
        // THE divergence gh#1054 was filed for, now pinned for both sockets by one test. A venue outage grows the
        // connect backoff toward its ceiling. When a connect finally succeeds, whatever was refusing connections has
        // demonstrably just stopped — and the work immediately after a reconnect (a burst of subscribes, or a full
        // snapshot request) is the likeliest moment of all to draw a rate limit. Charging that first attempt the
        // outage's accumulated delay leaves the socket CONNECTED, and therefore healthy-looking to everything else
        // in the process, while it delivers nothing for a full ceiling per retry.
        //
        // The retries are ridden on the CONNECTED branch, not on a socket held Disconnected (gh#1055 note 2): in
        // production the state after a successful connect IS Connected, and a fake that re-enters the connect branch
        // every pass would re-reset the backoff each time — proving "the reset happens on every connect" rather than
        // "the outage's backoff is not charged to what follows it".
        //
        // Asserted as CADENCE rather than as a stopwatch reading of one delay: after the reset every retry costs a
        // doubling from the poll interval, so six of them land well inside one ceiling, where the unreset ceiling
        // would spend six of them. The window sits an order of magnitude below the broken path and far above the
        // reset cadence, so a break fails on the ASSERTION with a legible message rather than on a timeout.
        TimeSpan ceiling = TimeSpan.FromMilliseconds(250);
        int connects = 0;
        long recoveredAt = 0;
        int attemptsAfterRecovery = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Disconnected until the connect recovers, then Connected — the branch the retries actually ride.
        SocketReadsAs(_ => Volatile.Read(ref recoveredAt) == 0
            ? ClientModels.ConnectionState.Disconnected
            : ClientModels.ConnectionState.Connected);
        ArrangeConnect(() =>
        {
            // Eight failures walk the backoff 1 → 2 → 4 … up to the ceiling; every attempt after that succeeds.
            if (Interlocked.Increment(ref connects) <= 8)
            {
                throw new InvalidOperationException("the venue is unreachable (test)");
            }

            Interlocked.CompareExchange(ref recoveredAt, Stopwatch.GetTimestamp(), 0);
            return Task.CompletedTask;
        });
        await ArrangePostConnectWorkAsync(() =>
        {
            if (Interlocked.Increment(ref attemptsAfterRecovery) >= 6)
            {
                retried.TrySetResult();
            }

            throw new InvalidOperationException("the venue rate-limited the post-connect work (test)");
        });

        BackgroundService host = CreateHost(Registered(), pollInterval: TimeSpan.FromMilliseconds(1), ceiling);
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should()
            .BeTrue("the post-connect obligation must keep being retried after the connect recovered");
        TimeSpan sinceRecovery = Stopwatch.GetElapsedTime(Volatile.Read(ref recoveredAt));
        await StopAsync(host);

        sinceRecovery.Should().BeLessThan(
            ceiling * 3,
            "six retries after a recovered connect must cost poll intervals, not the outage's accumulated ceiling");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldGrowTheBackoff_WhileTheConnectKeepsFailing_SoAnOutageIsNotHammered()
    {
        // The other half of the same invariant, and the one a reset test cannot pin: a loop that never grew its
        // backoff would satisfy the reset test trivially while retrying a refusing venue at full cadence for the
        // life of the outage — which is how a rate limit is sustained rather than relieved.
        //
        // The cadence here is deliberately coarse. Windows' default timer tick is ~15.6 ms, so a 1 ms poll interval
        // and a 1 ms first backoff are indistinguishable from each other; at 20 ms the growth is real time rather
        // than rounding, and the flat-backoff mutation lands an order of magnitude below the threshold.
        TimeSpan poll = TimeSpan.FromMilliseconds(20);
        int attempts = 0;
        long startedAt = Stopwatch.GetTimestamp();
        TaskCompletionSource kept = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() =>
        {
            // 20 + 40 + 80 + 160 + 320 = 620 ms of backoff before the sixth attempt, against 5 x 20 = 100 ms flat.
            if (Interlocked.Increment(ref attempts) >= 6)
            {
                kept.TrySetResult();
            }

            throw new InvalidOperationException("the venue is unreachable (test)");
        });

        BackgroundService host = CreateHost(Registered(), poll, maxBackoff: TimeSpan.FromMilliseconds(400));
        await host.StartAsync(CancellationToken.None);

        (await Signalled(kept)).Should().BeTrue("an outage longer than one attempt must still be retried");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        await StopAsync(host);

        elapsed.Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(300),
            "six connect attempts must be spread by a growing backoff, not retried at the raw poll interval");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2 · The Connecting / Reconnecting wait.
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ClientModels.ConnectionState.Connecting)]
    [InlineData(ClientModels.ConnectionState.Reconnecting)]
    public async Task ExecuteAsync_ShouldNeitherConnectNorAct_WhileTheClientIsAlreadyAttemptingIt(
        ClientModels.ConnectionState state)
    {
        // The client's own reconnect finishes MORE work than the manual connect does — it replays subscriptions on
        // the market-data socket and sends the sync on the trading one. Racing it with a manual connect would tear
        // that attempt down and land on the path that does neither, turning a self-healing drop into a silent
        // socket. An in-progress state therefore means "wait", exactly as it reads as "down" for liveness.
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        SocketIs(state);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await StopAsync(host);

        AssertNeverConnected();
        AssertNeverDidPostConnectWork();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReconnect_WhileTheSocketIsAlreadyConnected()
    {
        // The manual connect rebuilds the transport: driving it over a healthy socket would drop every subscription
        // it carries. A connected socket is never torn down just to finish work on it.
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        SocketIs(ClientModels.ConnectionState.Connected);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(4)).Should().BeTrue("the assertion below only means something if passes really ran");
        await StopAsync(host);

        AssertNeverConnected();
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3 · The unrecognised-state default.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldWaitRatherThanAct_WhenTheSocketReportsAnUnrecognisedState()
    {
        // An unknown state is not evidence the socket is usable, and acting on it could tear down a working
        // transport. The switch is a closed set with a fail-safe default rather than a blacklist that would let a
        // future ConnectionState member fall through into "treat as healthy" or "reconnect now" — and since the
        // enum lives in a vendored client this repo does not own, a new member is a routine event, not a fantasy.
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        SocketIs((ClientModels.ConnectionState)99);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await StopAsync(host);

        AssertNeverConnected();
        AssertNeverDidPostConnectWork();
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4 · Containment. Nothing escapes ExecuteAsync, and no fault ends the loop.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPolling_WhenAPassThrows()
    {
        // A transient fault must not stop the application, and it must not stop the LOOP either: a host that
        // exited on the first bad pass would leave the socket unmanaged for the life of the process while
        // ExecuteTask sat there completed-and-not-faulted, which nothing observes. Both halves are asserted —
        // the run is still going, and it is not faulted.
        SocketReadsAs(_ => throw new InvalidOperationException("reading the socket state failed (test)"));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await PassesObserved(4)).Should()
            .BeTrue("a pass that threw must be logged and retried, not end the loop");
        host.ExecuteTask!.IsCompleted.Should().BeFalse("the host is still running");
        await StopAsync(host);

        AssertNeverConnected();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_WhenTheConnectAttemptFails()
    {
        // The gap the client leaves: its internal reconnect gives up after one failure and parks the socket in
        // Disconnected for the rest of the session. An outage lasting longer than a single attempt must still
        // recover on its own.
        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                retried.TrySetResult();
            }

            throw new InvalidOperationException("the venue is unreachable (test)");
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await StopAsync(host);

        // Post-connect work sent over a socket that failed to connect can only throw.
        AssertNeverDidPostConnectWork();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_WhenThePostConnectWorkFails_SoAConnectedSocketIsNeverLeftSilent()
    {
        // Work that failed after a connect leaves a CONNECTED socket that no later pass would revisit if the
        // obligation were cleared by the attempt rather than by the result: "connected" looks healthy. The
        // obligation therefore survives into the next pass.
        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        await ArrangePostConnectWorkAsync(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("the socket dropped mid-obligation (test)");
            }

            retried.TrySetResult();
            return Task.CompletedTask;
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDoItsPostConnectWork_OnTheSamePassAsAHostDrivenConnect()
    {
        // The manual connect finishes LESS work than the client's own reconnect does — it never replays and never
        // syncs — so a connect this host drove is always followed by this host's own obligation. Asserted SAME-PASS,
        // not "eventually": a host that deferred it to a later pass would be indistinguishable from one that does it
        // at once unless the pass is pinned, and deferring is exactly the shape of the bug, because the socket looks
        // healthy in the meantime.
        int connectPass = 0;
        int workPass = 0;
        TaskCompletionSource worked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        ArrangeConnect(() =>
        {
            connectPass = PassesSoFar;
            return Task.CompletedTask;
        });
        await ArrangePostConnectWorkAsync(() =>
        {
            if (workPass == 0)
            {
                workPass = PassesSoFar;
                worked.TrySetResult();
            }

            return Task.CompletedTask;
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(worked)).Should().BeTrue("a connect the host drove is finished by nothing else");
        await StopAsync(host);

        workPass.Should().Be(connectPass, "the socket is silent for every pass between the connect and the work");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5 · OperationCanceledException is a stop only when the stopping token says so.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_WhenAVenueTimeoutArrivesAsAnOperationCanceledException()
    {
        // A send that times out inside HttpClient surfaces as a TaskCanceledException carrying HttpClient's OWN
        // internal token, not this host's. A loop that treated any OCE as a clean stop would exit silently on the
        // first venue timeout, leaving the socket unmanaged while the application runs on and nothing is logged.
        // So an OCE is a stop ONLY when the stopping token has actually been signalled; otherwise it is a fault
        // like any other and the pass is retried.
        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                retried.TrySetResult();
            }

            throw new TaskCanceledException(
                "the venue did not answer in time (test)", null, new CancellationToken(canceled: true));
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should()
            .BeTrue("a venue timeout is a transient fault, not this host's stopping token");
        host.ExecuteTask!.IsCompleted.Should().BeFalse("the host must still be running after a venue timeout");
        await StopAsync(host);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6 · Lazy resolve from the root provider, and the stand-down triad.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenTheTradovateClientIsNotRegistered()
    {
        // Tradovate is not wired in every deployment. An unconfigured venue is an idle host, not a startup failure.
        BackgroundService host = Host(new ServiceCollection().BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);

        (await RanToCompletion(host.ExecuteTask!)).Should()
            .BeTrue("standing down means the run ENDS — a host that fell through to the poll loop hangs here");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenBuildingTheClientThrows()
    {
        // Missing or malformed Tradovate credentials throw while the client is CONSTRUCTED — which is why the
        // resolve is lazy, inside the run, rather than through the constructor: an eager injection would fail the
        // host's construction, and under the default StopHost behaviour that stops the platform, taking the
        // auto-flatten watchdog and the kill switch with it.
        ServiceCollection services = new();
        services.AddSingleton<ITradovateWebSocketClient>(
            _ => throw new InvalidOperationException("Tradovate credentials are not configured (test)."));
        BackgroundService host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);

        (await RanToCompletion(host.ExecuteTask!)).Should()
            .BeTrue("standing down means the run ENDS — a host that fell through to the poll loop hangs here");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotDriveTheSocket_WhenARequiredCollaboratorIsNotRegistered()
    {
        // A wiring defect: the client is present but the collaborator that makes the post-connect obligation
        // possible is not. Connecting anyway would produce the exact failure each host exists to prevent — a socket
        // it brought up but could never finish — so stand down loudly instead of half-driving it.
        ServiceCollection services = new();
        RegisterWithoutARequiredCollaborator(services);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        BackgroundService host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);

        (await RanToCompletion(host.ExecuteTask!)).Should()
            .BeTrue("standing down means the run ENDS — a host that fell through to the poll loop hangs here");
        await StopAsync(host);

        AssertNeverConnected();
    }

    // ---------------------------------------------------------------------------------------------------------
    // 7 · Clean exit.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldExitCleanly_OnShutdown()
    {
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        SocketIs(ClientModels.ConnectionState.Connected);
        BackgroundService host = Host();

        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(2)).Should().BeTrue();

        // StopAsync awaits the run, so a loop that ignored the stopping token would never return — which is why the
        // helper bounds the wait and asserts completion instead of letting the run hang. The other half is that the
        // run ended without a FAULT.
        await StopAsync(host);
    }
}
