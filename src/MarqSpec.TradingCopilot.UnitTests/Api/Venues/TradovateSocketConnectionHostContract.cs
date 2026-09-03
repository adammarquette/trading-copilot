using System.Diagnostics;
using FakeItEasy;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Venues;
using MarqSpec.TradingCopilot.Domain.Notifications;
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

    /// <summary>
    /// The test cadence for how long a socket must go without delivering before the operator is told, and how long
    /// it must deliver before the incident is closed (production: two minutes).
    /// </summary>
    /// <remarks>
    /// Comfortably longer than a pass at the 1 ms test poll interval, so a blip that recovers on the very next pass
    /// is genuinely inside the grace rather than merely racing it — and comfortably shorter than
    /// <see cref="Timeout"/>, so a test that waits for the advisory fails on its assertion rather than on the clock.
    /// </remarks>
    protected static TimeSpan DegradedGrace { get; } = TimeSpan.FromMilliseconds(300);

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
        IServiceProvider services, TimeSpan pollInterval, TimeSpan maxBackoff, TimeSpan degradedGrace);

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

    /// <summary>
    /// The name this host gives its socket — the string its degraded-advisory dedup key is scoped by (gh#1051).
    /// </summary>
    /// <remarks>
    /// Declared per suite on purpose. The two suites name different sockets, so a base that shared one key between
    /// the hosts — which would let a degraded quote feed suppress a degraded trading feed — fails one of them.
    /// </remarks>
    protected abstract string SocketNameUnderTest { get; }

    // ---------------------------------------------------------------------------------------------------------
    // Shared harness.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The operator channel both hosts escalate through (gh#1051). Every send and resolve is recorded.</summary>
    protected RecordingNotificationChannel Notifications { get; } = new();

    /// <summary>A provider with everything the host needs.</summary>
    /// <remarks>
    /// The notification channel is registered <b>scoped</b>, exactly as production binds it (the outbox seam writes
    /// through a scoped <c>DbContext</c>, gh#437), and the provider validates scopes. A host that resolved the
    /// channel once from the root provider and held it — the obvious shortcut — would therefore fail here rather
    /// than in production, where the captive dependency surfaces as an <c>ObjectDisposedException</c> at the moment
    /// it is asked to page.
    /// </remarks>
    protected IServiceProvider Registered()
    {
        ServiceCollection services = new();
        Register(services);
        services.AddScoped<INotificationChannel>(_ => Notifications);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Builds the host over <see cref="Registered"/> at the default test cadence.</summary>
    protected BackgroundService Host(IServiceProvider? services = null) =>
        CreateHost(
            services ?? Registered(),
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxBackoff: TimeSpan.FromMilliseconds(2),
            degradedGrace: DegradedGrace);

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
        //
        // The ceiling is 1s rather than the 250ms it started at (gh#1070). The invariant is a RATIO -- six retries
        // at the reset cadence versus six at the accumulated ceiling -- but the budget it was checked against was an
        // absolute 750ms, and six Task.Delay round trips do not fit in 750ms on a loaded CI runner: Windows' default
        // timer tick alone is ~15.6ms, and the suite runs thousands of tests in parallel collections. It reddened on
        // two consecutive runs of #1069, which adds tests to this assembly, and passed every time in isolation.
        // Raising the ceiling scales the budget with the thing it is measuring instead of against wall-clock luck.
        // The pre-recovery walk is unaffected -- it doubles from the 1ms poll interval and never reaches the ceiling
        // in eight failures (~255ms), leaving the backoff at 256ms when the connect recovers.
        //
        // THE MARGIN IS SMALLER THAN IT LOOKS, AND IT IS A CLIFF. Measured at this ceiling, the mutant that deletes
        // the reset fails at 3s 838ms against the 3s threshold -- 838ms of margin, about 1.28x. NOT an order of
        // magnitude; that is true only of the correct path (~63ms of backoff against a 3s budget). The broken path
        // costs min(256,C) + min(512,C) + min(1024,C) + 2*min(2048,C) against a threshold of 3C, so for
        // 512 < C <= 1024 the margin is a flat 768ms however high C goes, and above 1024ms it SHRINKS linearly as
        // (1792 - C), reaching zero at C = 1792ms. So 1s sits at the top of the flat region with the full margin,
        // and anyone raising this further is walking toward an edge at ~1.79s where the guard silently stops killing
        // its mutant. Do not reach for this lever again without re-running that mutation.
        //
        // Why the cheap fix is nonetheless sound rather than a coin flip: a slow runner inflates the BROKEN path
        // further past the threshold, so the measured kill is a floor, not an average. All of the flakiness risk
        // sits on the correct-path side -- which is exactly the side this widens.
        //
        // This is the cheap fix, and it is the weak one: the test still measures the runner. Asserting on the retry
        // COUNT inside one ceiling, or driving the loop from an injected TimeProvider, is what makes it
        // deterministic -- gh#1070 carries that.
        TimeSpan ceiling = TimeSpan.FromSeconds(1);
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

        BackgroundService host = CreateHost(
            Registered(), pollInterval: TimeSpan.FromMilliseconds(1), ceiling, DegradedGrace);
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

        BackgroundService host = CreateHost(
            Registered(), poll, maxBackoff: TimeSpan.FromMilliseconds(400), DegradedGrace);
        await host.StartAsync(CancellationToken.None);

        (await Signalled(kept)).Should().BeTrue("an outage longer than one attempt must still be retried");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        await StopAsync(host);

        elapsed.Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(300),
            "six connect attempts must be spread by a growing backoff, not retried at the raw poll interval");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBackOff_WhileAConnectedSocketStillOwesItsPostConnectWork()
    {
        // The backoff is not the connect path's alone. A socket that is UP and still owes work — a quote key that
        // has not resubscribed, a snapshot that has not landed — is retried on the connected branch, and the usual
        // reason that work fails is the same rate limit a failed connect draws. Retrying it at the raw poll interval
        // would sustain the limit rather than relieve it, and would log an error every interval for the life of the
        // process while the socket goes on looking healthy.
        //
        // Coarse cadence for the same reason as the growth test: at 20 ms the doubling is real time rather than
        // Windows' ~15.6 ms timer rounding.
        TimeSpan poll = TimeSpan.FromMilliseconds(20);
        int attempts = 0;
        long connectedAt = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        ArrangeConnect(() =>
        {
            Interlocked.CompareExchange(ref connectedAt, Stopwatch.GetTimestamp(), 0);
            return Task.CompletedTask;
        });
        await ArrangePostConnectWorkAsync(() =>
        {
            // 20 + 40 + 80 + 160 + 320 = 620 ms before the sixth attempt, against 5 x 20 = 100 ms unbacked-off.
            if (Interlocked.Increment(ref attempts) >= 6)
            {
                retried.TrySetResult();
            }

            throw new InvalidOperationException("the venue rate-limited the post-connect work (test)");
        });

        BackgroundService host = CreateHost(
            Registered(), poll, maxBackoff: TimeSpan.FromMilliseconds(400), DegradedGrace);
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue("a connected socket that owes work must keep being retried");
        TimeSpan sinceConnect = Stopwatch.GetElapsedTime(Volatile.Read(ref connectedAt));
        await StopAsync(host);

        sinceConnect.Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(300),
            "a connected socket that still owes work must back off, not retry at the raw poll interval");
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
    public async Task ExecuteAsync_ShouldRetryAndBackOff_WhenAConnectTimesOutAsAnOperationCanceledException()
    {
        // A send that times out inside HttpClient surfaces as a TaskCanceledException carrying HttpClient's OWN
        // internal token, already cancelled — not this host's stopping token. It has to be handled as the ordinary
        // failed connect it is: logged, backed off, retried.
        //
        // The BACKOFF is what makes this discriminating, and the reason the assertion is a stopwatch rather than
        // "it tried twice". A connect wrapper that rethrew the OCE unconditionally — dropping the
        // `when (cancellationToken.IsCancellationRequested)` clause — still leaves the loop running, because the
        // pass-level handler catches it. What it loses is the pass's own conclusion: the pass ABORTS before it can
        // charge `delay = backoff`, so a refusing venue is retried at the raw poll interval forever. That is a rate
        // limit sustained rather than relieved, and nothing about the host's behaviour would look wrong.
        TimeSpan poll = TimeSpan.FromMilliseconds(20);
        int attempts = 0;
        long startedAt = Stopwatch.GetTimestamp();
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() =>
        {
            // 20 + 40 + 80 + 160 + 320 = 620 ms of backoff before the sixth attempt, against 5 x 20 = 100 ms if the
            // timeout aborts the pass instead of concluding it.
            if (Interlocked.Increment(ref attempts) >= 6)
            {
                retried.TrySetResult();
            }

            throw new TaskCanceledException(
                "the venue did not answer in time (test)", null, new CancellationToken(canceled: true));
        });

        BackgroundService host = CreateHost(
            Registered(), poll, maxBackoff: TimeSpan.FromMilliseconds(400), DegradedGrace);
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should()
            .BeTrue("a venue timeout is a transient fault, not this host's stopping token");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        host.ExecuteTask!.IsCompleted.Should().BeFalse("the host must still be running after a venue timeout");
        await StopAsync(host);

        elapsed.Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(300),
            "a timed-out connect is a failed connect: it must back off, not abort the pass and retry at full cadence");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPolling_WhenAPassIsCancelledByAForeignToken()
    {
        // The pass-level backstop for the same trap, one layer out. Anything inside a pass can raise an
        // OperationCanceledException that has nothing to do with this host's shutdown — a venue call surfacing
        // HttpClient's own cancelled token, a collaborator that wraps one. A loop that treated ANY OCE as a clean
        // stop would end silently on the first one: the socket goes unmanaged for the life of the process,
        // ExecuteTask sits completed-and-not-faulted so no watchdog sees anything, and nothing is logged. The pass
        // handler therefore tests THIS host's stopping token, not the exception's own.
        SocketReadsAs(_ => throw new TaskCanceledException(
            "a collaborator's own token was cancelled (test)", null, new CancellationToken(canceled: true)));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await PassesObserved(4)).Should()
            .BeTrue("an OperationCanceledException that is not this host's stop must be retried, not obeyed");
        host.ExecuteTask!.IsCompleted.Should().BeFalse("the host is still running");
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


    // ---------------------------------------------------------------------------------------------------------
    // 8 · The operator hears about a socket that stops delivering — and hears it once (gh#1051).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldTellTheOperator_WhenTheSocketStopsDelivering()
    {
        // THE gap gh#1051 was filed for, and it is not trading-specific. Everything above leaves exactly one trace
        // when it fails forever: an ILogger line at the backoff cadence, which reaches an engineer reading
        // structured logs and never the operator. The socket meanwhile reports Connected or climbs back to it, so
        // nothing downstream can tell either — a feed that is dead and a feed that is quiet look identical.
        //
        // This asserts on the ABSENCE being reported, not on a happy path: pin the advisory that must fire, because
        // a test that checked what a healthy socket does would pass while a broken one went unnoticed.
        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Notifications.Sent(1)).Should().BeTrue("a socket that never recovers must reach the operator");
        await StopAsync(host);

        Notification advisory = Notifications.Notifications.Should().ContainSingle().Subject;
        advisory.Severity.Should().Be(
            NotificationSeverity.Notify, "a degraded venue feed is ADR-0019's P2 tier, not a page");
        advisory.DedupKey.Should().Contain(
            SocketNameUnderTest, "one socket's outage must never suppress the other socket's");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotTellTheOperator_WhenAFailedPassRecoversInsideTheGrace()
    {
        // The other half, and the one that keeps the advisory worth reading. A blip that clears well inside the
        // grace is not an incident; paging on it would spend ADR-0019 §4's noise budget on nothing, and a pager
        // that cries wolf gets muted — at which point it is strictly worse than no pager, because it manufactures
        // confidence.
        //
        // The wait is deliberately several times the grace: a host that started its outage clock and never reset it
        // would have advised long before this returns, so the emptiness below is a real observation rather than a
        // race the assertion happened to win.
        int attempts = 0;
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        ArrangeConnect(() => Interlocked.Increment(ref attempts) == 1
            ? throw new InvalidOperationException("one bad attempt (test)")
            : Task.CompletedTask);
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(DegradedGrace * 4);
        await StopAsync(host);

        Volatile.Read(ref attempts).Should().BeGreaterThan(0, "the blip must actually have happened");
        Notifications.Notifications.Should().BeEmpty("a blip that recovered inside the grace is not an incident");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTellTheOperatorOnlyOnce_WhileOneOutageContinues()
    {
        // The advisory would repeat at the poll cadence if the host re-sent every degraded pass. The dedup channel
        // would collapse the pushes, but each one still costs a durable outbox row, and relying on a downstream
        // layer to hide a producer's noise is how ADR-0019 §4's budget gets spent without anyone noticing.
        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Sent(1)).Should().BeTrue();
        (await PassesObserved(PassesSoFar + 20)).Should()
            .BeTrue("the assertion below only means something if passes really ran after the advisory");
        await StopAsync(host);

        Notifications.Notifications.Should().ContainSingle("one continuing outage is one incident");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveTheAdvisory_WhenTheSocketDeliversAgainForTheWholeGrace()
    {
        // Not tidiness. DedupingNotificationChannel is a process-lifetime singleton that releases a key ONLY through
        // ResolveAsync, so a producer that never resolves turns "one notification per outage" into "one per process
        // lifetime": the first outage delivers and every later, independent one is silently suppressed as a
        // duplicate — this very failure reproduced one layer down. That was the blocking finding on gh#1045, and it
        // is pinned here so it cannot be reintroduced on this path.
        int recovered = 0;
        SocketReadsAs(_ => Volatile.Read(ref recovered) == 0
            ? ClientModels.ConnectionState.Disconnected
            : ClientModels.ConnectionState.Connected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Sent(1)).Should().BeTrue("the outage must be reported before it can be resolved");

        // Only now let the socket come back, so the resolve cannot be an artefact of a race with the advisory.
        Interlocked.Exchange(ref recovered, 1);

        (await Notifications.Resolved(1)).Should().BeTrue("an incident that ended must be closed");
        await StopAsync(host);

        Notifications.Resolutions.Should().AllBeEquivalentTo(
            Notifications.Notifications[0].DedupKey, "the key resolved must be the key that was reported");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotResolveTheAdvisory_UntilTheSocketHasDeliveredForTheWholeGrace()
    {
        // The hysteresis, and the reason it is not decoration. Closing the incident on the first healthy pass lets a
        // socket that recovers and fails faster than the grace produce advise → resolve → advise indefinitely: a
        // push per flap, however good the dedup below is, which is ADR-0019 §4's budget spent by a producer rather
        // than by a real fault. Requiring sustained health makes a flapping socket the one continuing incident it
        // actually is.
        // Counted in PASSES, not measured against the wall clock, and that is what makes it deterministic. A
        // delivering stretch of exactly one pass starts the recovery clock and reads it again in the same pass, so
        // the elapsed time is ~0 whatever the machine is doing -- a stalled runner cannot manufacture a recovery
        // here, which an earlier wall-clock version of this test could and did (gh#1070's hazard, in this file).
        int healthyPasses = 0;
        SocketReadsAs(pass =>
        {
            // Down until the outage is reported, so the incident genuinely exists before anything can close it.
            if (Notifications.Notifications.Count == 0)
            {
                return ClientModels.ConnectionState.Disconnected;
            }

            // Then alternate every pass. Every healthy pass is real -- the socket delivers, and a host that
            // resolved on the first good one would close the incident right here -- but no stretch of them ever
            // lasts a whole grace, so none of them is a recovery.
            if (pass % 2 != 0)
            {
                return ClientModels.ConnectionState.Disconnected;
            }

            Interlocked.Increment(ref healthyPasses);
            return ClientModels.ConnectionState.Connected;
        });
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Sent(1)).Should().BeTrue("the outage must be reported before it can be wrongly closed");
        (await PassesObserved(PassesSoFar + 20)).Should().BeTrue("the flapping has to actually run");
        await StopAsync(host);

        Volatile.Read(ref healthyPasses).Should()
            .BeGreaterThan(1, "the socket must really have delivered on some passes, or nothing was tested");
        Notifications.Resolutions.Should()
            .BeEmpty("a socket that never delivers for a whole grace has not recovered, whatever a single pass said");
        Notifications.Notifications.Should()
            .ContainSingle("and because it was never resolved, it was never re-raised either");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotResolveAnything_WhenNoAdvisoryWasEverRaised()
    {
        // The mirror of the branch above. A resolve fired on every healthy pass would re-arm a key some OTHER
        // producer legitimately holds, and would clear an incident this host never reported.
        SocketIs(ClientModels.ConnectionState.Connected);
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(20)).Should().BeTrue("the assertion below only means something if passes really ran");
        await Task.Delay(DegradedGrace * 2);
        await StopAsync(host);

        Notifications.Resolutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTellTheOperator_WhenTheClientSitsInAnAttemptItNeverFinishes()
    {
        // CHANGED BY REVIEW, and the change is the point. This loop deliberately waits out an attempt in progress
        // rather than tearing it down — but "wait" is about what the loop DRIVES, not about what the operator is
        // told. Treating a mid-attempt pass as proving nothing meant a socket that reconnects faster than the grace
        // — the venue closing shortly after `authorize`, or the client's silence-timeout loop — never accumulated an
        // outage at all, which is exactly the reported-to-nobody state this advisory exists for.
        //
        // It does not close gh#1052: that card is about getting a wedged socket OUT of this state. Reporting "it has
        // not delivered for N" is true meanwhile, and true is the bar.
        SocketIs(ClientModels.ConnectionState.Connecting);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Notifications.Sent(1)).Should()
            .BeTrue("a socket stuck mid-attempt delivers nothing, whatever the loop is right to do about it");
        await StopAsync(host);

        AssertNeverConnected();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTryAgain_WhenTheChannelDoesNotAcceptTheAdvisory()
    {
        // A send that was not accepted never reached anybody, so recording it as "told them" would cost the entire
        // incident to one transient channel failure — the same mistake DedupingNotificationChannel avoids by
        // recording only on success, one layer up.
        Notifications.Accept = false;
        SocketIs(ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Notifications.Sent(2)).Should()
            .BeTrue("an advisory the channel refused must be attempted again, not counted as delivered");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillReportTheNextOutage_WhenAnEarlierResolveWasNeverConfirmed()
    {
        // Round-2 review, note c. Holding the incident open until the channel CONFIRMS the resolve looks careful and
        // is the opposite: DedupingNotificationChannel re-arms its key unconditionally before it forwards (gh#300),
        // so the hazard a retry would guard against cannot happen -- while the retry itself creates a real one. A
        // resolve that keeps coming back unconfirmed leaves the host believing an advisory is still outstanding, and
        // the next genuine outage is then never raised at all, because the advisory only fires while that belief is
        // false. Silence, reached by way of a guard against silence.
        //
        // Sequence: outage -> advisory -> recovery -> a resolve the channel refuses to confirm -> outage again. The
        // SECOND advisory is the assertion, and it is also why the recording channel hands out one witness per wait.
        int healthy = 0;
        SocketReadsAs(_ => Volatile.Read(ref healthy) == 1
            ? ClientModels.ConnectionState.Connected
            : ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        Notifications.ConfirmResolve = false;

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Sent(1)).Should().BeTrue("the first outage must be reported");

        Interlocked.Exchange(ref healthy, 1);
        (await Notifications.Resolved(1)).Should().BeTrue("the recovery must at least attempt to close it");

        Interlocked.Exchange(ref healthy, 0);

        (await Notifications.Sent(2)).Should()
            .BeTrue("an unconfirmed resolve must not cost the operator every later outage");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillReachTheOperator_WhenAnEarlierResolveWasAcceptedButLost()
    {
        // Round-3 review, and the failure the round-2 comment claimed could not happen. This host talks to the
        // OUTBOX seam; three layers below it QueuedNotificationChannel is a bounded channel with
        // BoundedChannelFullMode.DropWrite, and under any Drop mode TryWrite DISCARDS the item and returns true --
        // so that class's own "queue is full" branch cannot run, nothing is logged, and this host is told the
        // resolve was accepted. The key stays armed in DedupingNotificationChannel for the life of the process, and
        // every later outage is suppressed as a duplicate while each layer reports success. That is gh#1045's
        // blocking finding, reproduced by this producer.
        //
        // The assertion is on DELIVERIES, not attempts: the host attempted outage 2's advisory in the broken
        // version too, and every layer told it that worked. What the operator got is the only thing that matters.
        int healthy = 0;
        SocketReadsAs(_ => Volatile.Read(ref healthy) == 1
            ? ClientModels.ConnectionState.Connected
            : ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        Notifications.LoseNextResolve = true;

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Delivered(1)).Should().BeTrue("the first outage must reach the operator");

        Interlocked.Exchange(ref healthy, 1);
        (await Notifications.Resolved(1)).Should()
            .BeTrue("the recovery closes the incident -- and this is the resolve that is silently lost");

        Interlocked.Exchange(ref healthy, 0);

        (await Notifications.Delivered(2)).Should()
            .BeTrue("a resolve that was accepted and lost must not cost the operator every later outage");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReArmTheIncidentKeyOnlyOnce_NotOnEveryRetryWithinTheNextOutage()
    {
        // RENAMED and RE-FIXTURED (gh#1079 finding 1). The old fixture set Accept = false from the very start, so
        // NOTHING was ever advised: `advised` is only ever set from AdviseDegradedAsync's return, and
        // reArmBeforeNextAdvisory is only ever set inside the `if (advised && ...)` branch that closes an
        // incident -- with nothing ever advised, that branch never ran, so the flag was false on every pass
        // regardless of the guard this test's old name claimed to pin. Deleting `if (reArmBeforeNextAdvisory)`
        // entirely left the test green: `reArmBeforeNextAdvisory` was false either way, so `Resolutions` was
        // empty either way. The mutation was not expressible against that fixture.
        //
        // This fixture drives a genuine outage -> advised -> recovered -> closed sequence first, so
        // reArmBeforeNextAdvisory is actually TRUE going into the second outage. THEN the channel starts refusing,
        // so that second outage is retried several times. The bound: exactly one resolve closes the first
        // incident, exactly one more re-arms before the second outage's first advisory, and NONE of the retries
        // that follow it may produce a third -- deleting the guard makes every retry re-resolve, which is the
        // regression this name now actually pins.
        int healthy = 0;
        SocketReadsAs(_ => Volatile.Read(ref healthy) == 1
            ? ClientModels.ConnectionState.Connected
            : ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        // Outage 1: genuinely advised...
        (await Notifications.Delivered(1)).Should().BeTrue("the first outage must actually reach the operator");

        // ...then closed, which is what arms reArmBeforeNextAdvisory for the outage that follows it.
        Interlocked.Exchange(ref healthy, 1);
        (await Notifications.Resolved(1)).Should().BeTrue("recovery must close the first incident");

        // Outage 2: the channel now refuses every send, so the host retries for the rest of the run -- one
        // initial attempt (which re-arms first) plus several retries that must NOT.
        Notifications.Accept = false;
        Interlocked.Exchange(ref healthy, 0);

        (await Notifications.Sent(4)).Should().BeTrue(
            "the second outage must really have retried several times beyond its first attempt");
        await StopAsync(host);

        Notifications.Resolutions.Should().HaveCount(2,
            "one resolve closes the first outage and one re-arms before the second outage's first advisory -- " +
            "none of that outage's later retries may produce a third");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopRetryingTheAdvisory_WhenAHeldDedupKeySurvivesTheReArm()
    {
        // gh#1079 finding 2. RecordingNotificationChannel.SendAsync now answers as the OUTBOX seam the host
        // actually holds -- true for an already-owed key, not the false DedupingNotificationChannel would answer
        // three layers below. This test is why that distinction is not academic.
        //
        // The round-3 re-arm (the test above) defends against ONE transient queue drop (gh#1077) -- it is not
        // proof against two. When the re-arm's OWN resolve is ALSO dropped, the key it was meant to release is
        // still held when the next outage's first advisory is sent. The outbox tells the host "recorded" --
        // seam-accurate, and exactly what production does -- so `advised` latches true and the loop stops
        // retrying, while the dedup layer three levels down has silently swallowed the only attempt this outage
        // ever made. This is gh#1045's failure shape, reproduced by a double that now tells the truth about which
        // seam it stands in for.
        int healthy = 0;
        SocketReadsAs(_ => Volatile.Read(ref healthy) == 1
            ? ClientModels.ConnectionState.Connected
            : ClientModels.ConnectionState.Disconnected);
        ArrangeConnect(() => throw new InvalidOperationException("the venue is unreachable (test)"));
        await ArrangePostConnectWorkAsync(() => Task.CompletedTask);
        Notifications.LoseNextResolve = true;

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await Notifications.Delivered(1)).Should().BeTrue("the first outage must reach the operator");

        Interlocked.Exchange(ref healthy, 1);
        (await Notifications.Resolved(1)).Should()
            .BeTrue("the recovery closes the incident -- and this is the resolve that is silently lost");

        // The re-arm ahead of the SECOND outage is also lost: two consecutive transient drops, not the single
        // one the round-3 defence covers.
        Notifications.LoseNextResolve = true;
        Interlocked.Exchange(ref healthy, 0);

        (await Notifications.Resolved(2)).Should()
            .BeTrue("the re-arm must still be attempted before the next advisory");
        (await Notifications.Sent(2)).Should().BeTrue("the second outage's advisory must still be attempted once");

        // The host believes the second advisory succeeded, so it must NOT retry it -- observe several more real
        // passes to prove that is a STUCK belief rather than merely a retry that has not happened yet.
        (await PassesObserved(PassesSoFar + 20)).Should()
            .BeTrue("the assertion below only means something if passes really ran after the second advisory");
        await StopAsync(host);

        Notifications.Notifications.Should().HaveCount(2,
            "the outbox told the host the second advisory was recorded, so it believes the incident is handled " +
            "and must not retry it, however many passes follow");
        Notifications.Deliveries.Should().ContainSingle(
            "the dedup layer held the key from the lost resolve, so the second advisory never actually reached " +
            "the operator -- advised stuck true over an operator who was told nothing");
    }

    /// <summary>Records what the host told the operator, so the advisory can be asserted rather than inferred.</summary>
    protected sealed class RecordingNotificationChannel : INotificationChannel
    {
        private readonly object _gate = new();
        private readonly List<Notification> _sent = [];
        private readonly List<Notification> _delivered = [];
        private readonly List<string> _resolved = [];

        // The dedup decorator's memory, modelled here -- THREE layers below the seam the host actually holds --
        // because the hazard this double exists to express lives in the interaction between it and the queue
        // above it: a key that is armed and never released suppresses every later incident, while every layer the
        // host can see reports success (gh#1079 finding 2). SendAsync below answers from the OUTBOX's point of
        // view, not this set's -- see the comment there.
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        // One witness PER WAIT, not one per channel. A single reusable TaskCompletionSource stays completed once it
        // has fired, so a second Sent(n) in the same test would return true off the FIRST wait's completion whatever
        // n was -- a wait that cannot fail, in the harness rather than in an assertion (gh#1051 round-2 review).
        private readonly List<(int At, TaskCompletionSource Signal)> _sentWaits = [];
        private readonly List<(int At, TaskCompletionSource Signal)> _resolvedWaits = [];
        private readonly List<(int At, TaskCompletionSource Signal)> _deliveredWaits = [];

        /// <summary>Whether a send is accepted for delivery — false is a channel that could not take it.</summary>
        public bool Accept { get; set; } = true;

        /// <summary>Whether a resolve is confirmed — false is a cancel the channel could not vouch for.</summary>
        public bool ConfirmResolve { get; set; } = true;

        /// <summary>
        /// Loses the <b>next</b> resolve — recorded, reported successful, key never released — then clears itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This models the real chain rather than an invented failure. <c>QueuedNotificationChannel</c> is a bounded
        /// channel with <c>BoundedChannelFullMode.DropWrite</c>, and under any Drop mode <c>TryWrite</c> discards
        /// the item and returns <see langword="true"/> — so its own "queue is full" branch cannot run, nothing is
        /// logged, and the caller three layers up is told the resolve was accepted while
        /// <c>DedupingNotificationChannel</c> goes on holding the key. Without this knob a held key is not
        /// representable here, and a test asserting the host "re-raises" proves only half the system (gh#1051
        /// round-3 review).
        /// </para>
        /// <para>
        /// One-shot, because a queue that overflows forever is a different (and louder) fault: the interesting case
        /// is the transient drop, where everything is healthy again by the next outage and the ONLY lasting damage
        /// is the key nobody released.
        /// </para>
        /// </remarks>
        public bool LoseNextResolve { get; set; }

        /// <summary>Everything the host asked the operator to be told, oldest first.</summary>
        public IReadOnlyList<Notification> Notifications
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sent];
                }
            }
        }

        /// <summary>What actually reached the operator — sends the dedup memory did not suppress.</summary>
        public IReadOnlyList<Notification> Deliveries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _delivered];
                }
            }
        }

        /// <summary>Every incident key the host closed, oldest first.</summary>
        public IReadOnlyList<string> Resolutions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _resolved];
                }
            }
        }

        /// <summary>Completes once <paramref name="count"/> sends have been attempted; false means it timed out.</summary>
        public Task<bool> Sent(int count) => Wait(_sentWaits, () => _sent.Count, count);

        /// <summary>Completes once <paramref name="count"/> resolves have landed; false means it timed out.</summary>
        public Task<bool> Resolved(int count) => Wait(_resolvedWaits, () => _resolved.Count, count);

        /// <summary>Completes once <paramref name="count"/> notifications have REACHED the operator.</summary>
        public Task<bool> Delivered(int count) => Wait(_deliveredWaits, () => _delivered.Count, count);

        /// <inheritdoc />
        /// <remarks>
        /// <b>Answers as the OUTBOX, not the dedup layer (gh#1079 finding 2).</b> The host's <c>INotificationChannel</c>
        /// resolves to <c>OutboxNotificationChannel</c>, three layers above <c>DedupingNotificationChannel</c> in the
        /// outbox → queue → dedup → transport chain (<c>NotificationRegistration.cs</c>) -- and
        /// <c>OutboxNotificationChannel.SendAsync</c> returns <see langword="true"/> for an already-owed row exactly
        /// as it does for a new one (<c>OutboxNotificationChannel.cs</c>, "Already owed? … success, not a
        /// collision"): dedup suppression happens later, below the queue, and its result never reaches this seam.
        /// Returning <see langword="false"/> here for a held key -- what the DECORATOR would answer -- would be
        /// faithful to a layer the host does not talk to, and would hide the failure shape this double exists to
        /// reproduce: a held key that leaves the host believing an advisory succeeded while the operator hears
        /// nothing (see <c>ExecuteAsync_ShouldStopRetryingTheAdvisory_WhenAHeldDedupKeySurvivesTheReArm</c>).
        /// </remarks>
        public Task<bool> SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _sent.Add(notification);
                Release(_sentWaits, _sent.Count);

                // Already held below -- the OUTBOX still says "recorded" (seam-accurate: true), but the dedup memory
                // modelled by _reported swallows it before it reaches the operator, so it is deliberately NOT added
                // to _delivered. The host is told success and stops retrying; the operator hears nothing. That gap
                // between "sent" and "delivered" IS the production symptom, not a benign retry loop.
                if (_reported.Contains(notification.DedupKey))
                {
                    return Task.FromResult(true);
                }

                if (!Accept)
                {
                    return Task.FromResult(false);
                }

                // Recorded ONLY on success, like the decorator: a push that never landed is not "already told them".
                _reported.Add(notification.DedupKey);
                _delivered.Add(notification);
                Release(_deliveredWaits, _delivered.Count);
            }

            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<bool> ResolveAsync(string dedupKey, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _resolved.Add(dedupKey);
                Release(_resolvedWaits, _resolved.Count);

                // The accepted-then-lost case: recorded, reported successful, key NEVER released. One-shot.
                if (LoseNextResolve)
                {
                    LoseNextResolve = false;
                }
                else
                {
                    _reported.Remove(dedupKey);
                }
            }

            return Task.FromResult(ConfirmResolve);
        }

        // `soFar` is a callback rather than a value so the count is read UNDER the gate: reading it at the call site
        // would let a send land between the read and the registration, and the wait would then miss the very event
        // it was registered for.
        private Task<bool> Wait(List<(int At, TaskCompletionSource Signal)> waits, Func<int> soFar, int count)
        {
            TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (soFar() >= count)
                {
                    signal.TrySetResult();
                }
                else
                {
                    waits.Add((count, signal));
                }
            }

            return Signalled(signal);
        }

        private static void Release(List<(int At, TaskCompletionSource Signal)> waits, int reached)
        {
            foreach ((int at, TaskCompletionSource signal) in waits)
            {
                if (reached >= at)
                {
                    signal.TrySetResult();
                }
            }
        }
    }
}
