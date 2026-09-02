using System.Diagnostics;
using FakeItEasy;
using MarqSpec.Client.Tradovate;
using MarqSpec.Client.Tradovate.Authentication;
using MarqSpec.Client.Tradovate.Exceptions;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Accounts;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using MarqSpec.TradingCopilot.UnitTests.Api.Venues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Accounts;

/// <summary>
/// The Tradovate trading-socket connection host (gh#977): the owner of the process-wide <b>trading</b> socket's
/// lifecycle — connect, reconnect, and the <c>user/syncrequest</c> that is the only thing which makes order,
/// position and fill events flow at all.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the vendored client leaves the trading socket in exactly the shape its market-data sibling is
/// left in, plus one worse gap. The client's internal reconnect <b>gives up after a single failed attempt</b> and
/// parks the socket in <see cref="ClientModels.ConnectionState.Disconnected"/>; and the <b>manual</b>
/// <c>ConnectTradingAsync</c> that is the only way back authorizes the socket but <b>never sends the sync
/// request</b> — the client issues that only on its own reconnect path. A socket recovered manually is therefore
/// connected, authorized, and permanently silent: Tradovate pushes <c>props</c> entity frames only to a socket that
/// has synced, so every order, fill and position update simply stops arriving with no exception anywhere.
/// </para>
/// <para>
/// <b>The loop itself is tested by the base class</b>, <see cref="TradovateSocketConnectionHostContract"/>, and the
/// market-data host inherits the same tests from the same source (gh#1054). What remains here is what genuinely
/// differs: this socket's <c>user/syncrequest</c> obligation, the grace pass that keeps a duplicate snapshot off the
/// ordinary path, and the <c>ConnectionStatusChanged</c> re-arm that only this socket needs — because only this one
/// can reach <c>Connected</c> unsynced without anything failing.
/// </para>
/// <para>
/// Every test that proves a <b>negative</b> ("it did not connect", "it did not sync") first waits on
/// <see cref="TradovateSocketConnectionHostContract.PassesObserved"/>, so the assertion rests on passes that really
/// ran rather than on a sleep the host might have spent doing nothing at all. Every wait is time-bound — a bare
/// <see cref="TaskCompletionSource"/> await that never completes hangs the whole run and reads as slow CI rather
/// than as a red test.
/// </para>
/// </remarks>
public class TradovateTradingConnectionHostTests : TradovateSocketConnectionHostContract
{
    private const long UserId = 42;

    private const long AuthMeUserId = 4242;

    private readonly IAuthenticationService _authentication = A.Fake<IAuthenticationService>();

    private readonly ITradovateApiClient _apiClient = A.Fake<ITradovateApiClient>();

    private readonly TradovateTradingSocketSync _sync = new();

    public TradovateTradingConnectionHostTests()
    {
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._)).Returns<long?>(UserId);
        A.CallTo(() => _apiClient.GetAuthMeAsync(A<CancellationToken>._))
            .Returns(new ClientModels.AuthMe { UserId = AuthMeUserId });
    }

    /// <inheritdoc />
    protected override BackgroundService CreateHost(
        IServiceProvider services, TimeSpan pollInterval, TimeSpan maxBackoff, TimeSpan degradedGrace) =>
        new TradovateTradingConnectionHost(
            services,
            NullLogger<TradovateTradingConnectionHost>.Instance,
            pollInterval,
            maxBackoff,
            degradedGrace);

    /// <inheritdoc />
    protected override void Register(ServiceCollection services)
    {
        services.AddSingleton(Client);
        services.AddSingleton(_authentication);
        services.AddSingleton(_apiClient);
        services.AddSingleton(_sync);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This host has <b>three</b> required collaborators — the authentication service, the REST client behind the
    /// <c>/auth/me</c> fallback, and the sync register nothing above could read without. Dropping all three at once
    /// satisfies the shared contract's stand-down test through whichever guard happens to run first, so it proves
    /// only that <i>some</i> guard exists; each one is pinned separately by
    /// <c>ExecuteAsync_ShouldNotDriveTheSocket_WhenExactlyOneCollaboratorIsMissing</c>.
    /// </remarks>
    protected override void RegisterWithoutARequiredCollaborator(ServiceCollection services) =>
        services.AddSingleton(Client);

    /// <inheritdoc />
    protected override string SocketNameUnderTest => "trading";

    /// <inheritdoc />
    protected override void ArrangeSocketState(Func<ClientModels.ConnectionState> read) =>
        A.CallTo(() => Client.TradingState).ReturnsLazily(read);

    /// <inheritdoc />
    protected override void ArrangeConnect(Func<Task> behaviour) =>
        A.CallTo(() => Client.ConnectTradingAsync(A<CancellationToken>._)).ReturnsLazily(behaviour);

    /// <inheritdoc />
    /// <remarks>
    /// The real client raises <c>SyncCompleted</c> from inside <c>SyncRequestAsync</c>, before it returns — and only
    /// when the request actually landed. Mirroring both halves on the fake is what makes "the host stops asking once
    /// a snapshot has landed" a property of the host rather than of this test's setup.
    /// </remarks>
    protected override Task ArrangePostConnectWorkAsync(Func<Task> behaviour)
    {
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                await behaviour();
                RaiseSyncCompleted();
                return new ClientModels.SyncResult();
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void AssertNeverConnected() =>
        A.CallTo(() => Client.ConnectTradingAsync(A<CancellationToken>._)).MustNotHaveHappened();

    /// <inheritdoc />
    protected override void AssertNeverDidPostConnectWork() =>
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();

    // The real client raises SyncCompleted from inside SyncRequestAsync, before it returns.
    private void SyncRaisesCompletion(Action? onSync = null)
    {
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                onSync?.Invoke();
                RaiseSyncCompleted();
                return Task.FromResult(new ClientModels.SyncResult());
            });
    }

    private void RaiseSyncCompleted() =>
        Client.SyncCompleted += Raise.With(Client, new ClientModels.SyncResult());

    // The client raises this from SetState, synchronously, on every transition -- including the ones NOTHING in this
    // host drives: its internal reconnect rebuilds the socket on a background task and reports Connected without the
    // host ever calling connect. That transition is the host's only cue that a live connection carries no entity
    // subscription, so it has to be exercised rather than assumed.
    private void RaiseConnected(bool tradingSocket) =>
        Client.ConnectionStatusChanged += Raise.With(Client, new ClientModels.ConnectionStatusChange
        {
            IsTradingSocket = tradingSocket,
            Previous = ClientModels.ConnectionState.Reconnecting,
            Current = ClientModels.ConnectionState.Connected,
        });

    [Fact]
    public async Task ExecuteAsync_ShouldConnectTheTradingSocket_WhenItIsDisconnected()
    {
        // The client never opens the socket on its own, and its internal reconnect only ever runs after a socket
        // that was up went down — so at startup somebody has to open it, and that is this host and only this host.
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        A.CallTo(() => Client.ConnectTradingAsync(A<CancellationToken>._)).Invokes(() => connected.TrySetResult());
        SyncRaisesCompletion();

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(connected)).Should().BeTrue();
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendTheSyncRequest_OnTheSamePassAsAHostDrivenConnect()
    {
        // THE point of the host. ConnectTradingAsync authorizes the socket and stops there — the client sends
        // user/syncrequest only from its OWN reconnect path — and Tradovate pushes props entity frames only to a
        // socket that has synced. So a host-driven connect that did not sync leaves the socket connected and
        // permanently silent: no order, fill or position event, and nothing raised.
        int connectPass = 0;
        int syncPass = 0;
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => Client.ConnectTradingAsync(A<CancellationToken>._))
            .Invokes(() => connectPass = PassesSoFar);
        SyncRaisesCompletion(() =>
        {
            syncPass = PassesSoFar;
            synced.TrySetResult();
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue("a connect the host drove is never synced by anything else");
        await StopAsync(host);

        syncPass.Should().Be(connectPass, "the socket is silent for every pass between the connect and the sync");
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendTheSyncRequestForTheAuthenticatedUser()
    {
        // user/syncrequest subscribes the socket to ONE user's entities. Syncing the wrong user (or none) returns a
        // snapshot with nothing in it and subscribes nothing — the same silent socket, with a successful call behind
        // it. This is the id the client's own reconnect uses, read from the same service.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion(() => synced.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await StopAsync(host);

        A.CallTo(() => Client.SyncRequestAsync(
                A<ClientModels.SyncRequest>.That.Matches(request => request.Users.SequenceEqual(new[] { UserId })),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendTheSyncRequest_WhenTheSocketIsFoundConnectedAndNothingSyncedIt()
    {
        // The gap the client's own reconnect can still leave without failing: it skips the sync entirely when the
        // authenticated user id is unavailable, and it reports Connected either way. A socket that is up and has
        // never synced is the silent one, so "connected" alone is not evidence the feed is alive. This is where the
        // two sockets legitimately part company — the market-data host does nothing on a healthy socket.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion(() => synced.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await StopAsync(host);

        // A connected socket must never be torn down just to sync it: the manual connect rebuilds the transport.
        AssertNeverConnected();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSendASyncRequest_WhenTheClientsOwnReconnectSyncsFirst()
    {
        // The grace pass. The client's internal reconnect sends the sync itself, in the statement after it reports
        // Connected — so a host that synced on the first pass it saw Connected would put a duplicate
        // user/syncrequest on the ORDINARY path, and what Tradovate does with a second one is unpinned here.
        //
        // The completion is raised on the SECOND state read, which is after the first pass has already decided the
        // sync is due. A host without the grace would have sent it on that first pass, so this fails against the
        // eager implementation rather than merely passing alongside it.
        SocketReadsAs(read =>
        {
            if (read == 2)
            {
                RaiseSyncCompleted();
            }

            return ClientModels.ConnectionState.Connected;
        });
        SyncRaisesCompletion();

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(6)).Should().BeTrue("the assertion below only means something if passes really ran");
        await StopAsync(host);

        AssertNeverDidPostConnectWork();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopSendingSyncRequests_OnceOneHasCompleted()
    {
        // A sync is a snapshot of every order, fill and position the user owns. Re-sending it every pass would
        // re-deliver all of it to every consumer for the life of the process, so the need must be cleared by the
        // snapshot landing rather than by the socket looking healthy.
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion();

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(8)).Should().BeTrue();
        await StopAsync(host);

        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSyncAgain_WhenTheTradingSocketReconnectsAfterAnEarlierSyncHadSettledIt()
    {
        // The recovery path nothing else covers, and the one the socket spends most of its life on. Once a snapshot
        // has landed the need is None, and a socket that drops and is rebuilt by the CLIENT never passes through
        // Disconnected as far as this host's poll is concerned: the client's background reconnect can rebuild and
        // report Connected between two passes. The only cue that the live connection carries no entity subscription
        // is the transition itself — so if the host does not re-arm on it, a reconnect whose own sync was skipped
        // (a null user id: no failure, no exception) leaves the socket connected, authorized and silent until the
        // process restarts.
        //
        // Written to fail against the ABSENCE of the handler, not merely alongside it: every other Connected-path
        // test in this class rides the field's initial Pending, so removing the subscription leaves them all green.
        int syncs = 0;
        int reconnected = 0;
        TaskCompletionSource resynced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketReadsAs(_ =>
        {
            // Once, and only after a snapshot has already settled the need to None.
            if (Volatile.Read(ref syncs) >= 1 && Interlocked.Exchange(ref reconnected, 1) == 0)
            {
                RaiseConnected(tradingSocket: true);
            }

            return ClientModels.ConnectionState.Connected;
        });
        SyncRaisesCompletion(() =>
        {
            if (Interlocked.Increment(ref syncs) == 2)
            {
                resynced.TrySetResult();
            }
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(resynced)).Should()
            .BeTrue("a reconnect the client drove carries no entity subscription until something syncs it");
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSyncAgain_WhenItWasTheMarketDataSocketThatReconnected()
    {
        // One client, two sockets, one event. A market-data reconnect says nothing about whether the TRADING socket
        // still carries its entity subscription — so treating it as a cue would put a duplicate full snapshot of
        // every order, fill and position on the ordinary path every time a quote feed blipped, which is exactly the
        // cost the grace pass exists to avoid. This is the test that makes the IsTradingSocket filter load-bearing.
        int syncs = 0;
        int reconnected = 0;
        SocketReadsAs(_ =>
        {
            if (Volatile.Read(ref syncs) >= 1 && Interlocked.Exchange(ref reconnected, 1) == 0)
            {
                RaiseConnected(tradingSocket: false);
            }

            return ClientModels.ConnectionState.Connected;
        });
        SyncRaisesCompletion(() => Interlocked.Increment(ref syncs));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(10)).Should().BeTrue("the assertion below only means something if passes really ran");
        await StopAsync(host);

        Volatile.Read(ref reconnected).Should().Be(1, "the market-data reconnect must actually have been raised");
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryTheSyncRequest_WhenItFails_SoAConnectedSocketIsNeverLeftSilent()
    {
        // A failed sync leaves a CONNECTED socket that no later pass would revisit if the need were cleared by the
        // attempt rather than by the snapshot: "connected" looks healthy. The need therefore survives into the next
        // pass, and the retry rides the connect path's backoff — a sync usually fails for the same reason a connect
        // does, a rate limit above all, and retrying at full cadence would sustain it.
        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("the socket dropped mid-sync (test)");
                }

                retried.TrySetResult();
                RaiseSyncCompleted();
                return Task.FromResult(new ClientModels.SyncResult());
            });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await StopAsync(host);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResetTheBackoff_WhenASnapshotLands_SoALaterReArmedSyncIsNotChargedTheFailures()
    {
        // The second half of the shared reset rule — "a pass that owes nothing returns the cadence to the poll
        // interval" — and the one that cannot live in the shared contract. It is only observable through a LATER
        // pass that owes something, and only this socket can be pushed back into owing one without a reconnect:
        // ConnectionStatusChanged re-arms it. Market data's obligation, once settled, is re-armed only by a
        // host-driven connect, which resets the backoff on its own path anyway. The code being pinned is shared, so
        // the mutation dies for both hosts even though only this suite can stage it.
        //
        // Sequence: several failed syncs walk the backoff to its ceiling · one lands, which must reset it · the
        // socket is re-armed by a client-driven reconnect · the syncs fail again. Those retries must cost poll
        // intervals doubling from the reset, not the ceiling the earlier failures reached.
        TimeSpan ceiling = TimeSpan.FromMilliseconds(250);
        int attempts = 0;
        long reArmedAt = 0;
        int failuresAfterReArm = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SocketReadsAs(_ =>
        {
            // Re-arm once, immediately after the snapshot that settled the need — the client's own reconnect
            // rebuilding the socket between two passes.
            if (Volatile.Read(ref attempts) >= 9 && Interlocked.CompareExchange(ref reArmedAt, Stopwatch.GetTimestamp(), 0) == 0)
            {
                RaiseConnected(tradingSocket: true);
            }

            return ClientModels.ConnectionState.Connected;
        });
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily<Task<ClientModels.SyncResult>>(() =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt < 9)
                {
                    // Eight failures walk the backoff 1 → 2 → 4 … to the ceiling.
                    throw new TradovateRateLimitException("the venue rate-limited the sync (test)");
                }

                if (attempt == 9)
                {
                    // The snapshot that lands. THIS is the pass whose reset is under test.
                    RaiseSyncCompleted();
                    return Task.FromResult(new ClientModels.SyncResult());
                }

                if (Interlocked.Increment(ref failuresAfterReArm) >= 6)
                {
                    retried.TrySetResult();
                }

                throw new TradovateRateLimitException("the venue rate-limited the sync again (test)");
            });

        BackgroundService host = new TradovateTradingConnectionHost(
            Registered(),
            NullLogger<TradovateTradingConnectionHost>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxBackoff: ceiling,
            degradedGrace: DegradedGrace);
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue("a re-armed sync must keep being retried");
        TimeSpan sinceReArm = Stopwatch.GetElapsedTime(Volatile.Read(ref reArmedAt));
        await StopAsync(host);

        sinceReArm.Should().BeLessThan(
            ceiling * 4,
            "the pass whose snapshot landed must reset the backoff, so the re-armed retries cost poll intervals");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_AndNotSync_WhenNeitherSourceReportsAnAuthenticatedUserId()
    {
        // Without the user id there is no sync request to build. The failure direction is "keep trying and stay
        // loud" rather than "give up quietly": the id comes from the token response, so a later renewal can supply
        // one — and a socket that is up and unsynced is silent, which is the state that must never be settled into.
        int reads = 0;
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref reads) >= 2)
            {
                asked.TrySetResult();
            }

            return Task.FromResult<long?>(null);
        });
        A.CallTo(() => _apiClient.GetAuthMeAsync(A<CancellationToken>._))
            .Returns(new ClientModels.AuthMe { UserId = null });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(asked)).Should().BeTrue("an unavailable user id is retried, not settled into");
        await StopAsync(host);

        AssertNeverDidPostConnectWork();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The user id has two sources, because the primary one can simply be missing (gh#1051).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldSyncWithTheUserIdFromAuthMe_WhenTheTokenResponseOmittedIt()
    {
        // The path that used to be terminal. GetUserIdAsync returns null when the server omitted the id from the
        // token response — no failure, no exception — which is exactly the case the client's own reconnect answers
        // by skipping its sync and reporting Connected anyway. A host with only that source loops on it forever
        // over a connected, silent socket. /auth/me returns the same id from a different endpoint, so the omission
        // stops being terminal.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._)).Returns<long?>(null);
        SyncRaisesCompletion(() => synced.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue("a token response without a user id is not the end of the road");
        await StopAsync(host);

        A.CallTo(() => Client.SyncRequestAsync(
                A<ClientModels.SyncRequest>.That.Matches(
                    request => request.Users.SequenceEqual(new[] { AuthMeUserId })),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotAskAuthMe_WhenTheTokenResponseAlreadyCarriesTheUserId()
    {
        // The fallback is a fallback. Asking /auth/me on every sync would put a REST round trip on the ordinary
        // path — and on a venue whose usual failure mode is a rate limit, spending a request to learn something
        // already in hand is how the sync that matters gets refused.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion(() => synced.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await StopAsync(host);

        A.CallTo(() => _apiClient.GetAuthMeAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotSync_WhenAuthMeReportedAnErrorAlongsideAUserId()
    {
        // Tradovate reports REST failures in the BODY rather than the status, so a 200 carrying ErrorText is a
        // failure and the id beside it is not to be trusted. Syncing on it would subscribe the socket to the wrong
        // user, which returns an empty snapshot and subscribes nothing — the same silent socket, with a successful
        // call behind it.
        int reads = 0;
        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._)).Returns<long?>(null);
        A.CallTo(() => _apiClient.GetAuthMeAsync(A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref reads) >= 2)
            {
                asked.TrySetResult();
            }

            return Task.FromResult(new ClientModels.AuthMe { ErrorText = "not authorized", UserId = AuthMeUserId });
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(asked)).Should().BeTrue("a rejected /auth/me is retried, not settled into");
        await StopAsync(host);

        AssertNeverDidPostConnectWork();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillSync_WhenReadingTheTokenResponsesUserIdThrows()
    {
        // A failing token service must not mean an unsyncable socket: the id is available from a second endpoint,
        // so the fallback covers a throw for the same reason it covers a null.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("the token service is unreachable (test)"));
        SyncRaisesCompletion(() => synced.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await StopAsync(host);
    }

    // ---------------------------------------------------------------------------------------------------------
    // A completion that lands after a fresh connect (gh#1051).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldSyncAgain_WhenTheSocketReconnectedWhileTheHostsOwnSyncWasStillInFlight()
    {
        // The third path gh#1051 records. The client fails every request still PENDING as it rebuilds the transport,
        // but a response its receive loop has already dispatched resolves regardless — so a sync started on one
        // connection can complete after a fresh one has re-armed the need. A host that cleared on that completion
        // would leave the NEW connection connected, authorized and silent for the life of the process, and nothing
        // would ever revisit it because the socket reports Connected throughout.
        //
        // Written to fail against the unconditional clear: the reconnect and the completion are raised in that order
        // from inside the sync call, which is exactly where the real client raises both.
        int syncs = 0;
        int raced = 0;
        TaskCompletionSource resynced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                if (Interlocked.Exchange(ref raced, 1) == 0)
                {
                    // The transport was rebuilt underneath this request, and the new connection carries no entity
                    // subscription — while this request's own answer is already on its way back.
                    RaiseConnected(tradingSocket: true);
                }

                RaiseSyncCompleted();
                if (Interlocked.Increment(ref syncs) == 2)
                {
                    resynced.TrySetResult();
                }

                return Task.FromResult(new ClientModels.SyncResult());
            });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(resynced)).Should()
            .BeTrue("a snapshot for a connection that no longer exists does not sync the one that replaced it");
        await StopAsync(host);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The synced state is a fact something else can read (gh#1051).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldPublishThatTheSocketIsSynced_OnceASnapshotHasLanded()
    {
        // TradingState reports Connected whether or not the socket was ever subscribed, so this is the ONLY thing
        // that lets TradovateAccountEventStream tell a silent socket from a quiet account. The host holding the
        // answer privately — which is what it did before — is the same information loss one layer in.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion(() => synced.TrySetResult());
        _sync.IsSynced.Should().BeFalse("nothing has synced this socket yet");

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await StopAsync(host);

        _sync.IsSynced.Should().BeTrue("the snapshot landed, so entity frames are flowing");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPublishThatTheSocketIsNotSynced_WhileTheSyncKeepsFailing()
    {
        // The half that matters. A consumer that read "synced" off a socket whose sync has never landed would open a
        // stream over it and report a quiet account — which is the one thing auto-flatten must never be told.
        TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        SocketIs(ClientModels.ConnectionState.Connected);
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily<Task<ClientModels.SyncResult>>(() =>
            {
                if (Interlocked.Increment(ref attempts) >= 2)
                {
                    attempted.TrySetResult();
                }

                throw new TradovateRateLimitException("the venue rate-limited the sync (test)");
            });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(attempted)).Should().BeTrue();
        await StopAsync(host);

        _sync.IsSynced.Should().BeFalse("a sync that never landed leaves the socket connected and silent");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The grace pass is not an all-clear (gh#1051 review, finding 2).
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShouldNotResolveTheAdvisory_WhenASocketThatKeepsReconnectingIsStillUnsynced()
    {
        // The false all-clear. The grace pass sends nothing on the wire, so it must not charge the backoff -- and
        // the first version of this change reported that as "the socket owes nothing", which the shared loop reads
        // as an all-clear. A socket that reconnects at least once per grace therefore closed its own incident and
        // reset the outage clock forever: connected, degraded, and nobody told, which is the exact state this card
        // was filed for, reintroduced by the card's own fix.
        //
        // Here the socket reconnects continuously and the sync never lands, so EVERY pass is either the grace pass
        // or a failed sync. Nothing is ever delivering, so nothing may ever be resolved.
        SocketReadsAs(_ =>
        {
            // A fresh connection on every pass -- the venue closing shortly after `authorize`, or the client's own
            // silence-timeout reconnect loop.
            RaiseConnected(tradingSocket: true);
            return ClientModels.ConnectionState.Connected;
        });
        A.CallTo(() => Client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily<Task<ClientModels.SyncResult>>(
                () => throw new TradovateRateLimitException("the venue rate-limited the sync (test)"));

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Notifications.Sent(1)).Should()
            .BeTrue("a socket that reconnects forever and never syncs must still reach the operator");
        await Task.Delay(DegradedGrace * 3);
        await StopAsync(host);

        Notifications.Resolutions.Should()
            .BeEmpty("a grace pass has PROVED the socket is unsynced; it is the last pass that may read as healthy");
        _sync.IsSynced.Should().BeFalse("nothing ever synced this socket");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Each fail-safe collaborator guard stands the host down on its own (gh#1051 review, finding 3).
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("authentication")]
    [InlineData("api-client")]
    [InlineData("sync-register")]
    public async Task ExecuteAsync_ShouldNotDriveTheSocket_WhenExactlyOneCollaboratorIsMissing(string missing)
    {
        // Per-collaborator, because the shared contract's stand-down test drops all three at once -- so the
        // pre-existing authentication guard satisfied it alone and either new guard could be deleted with the suite
        // still green. The sync-register one in particular is what keeps the Sync property from throwing on every
        // pass; the api-client one is what keeps a token response without a user id from being terminal.
        //
        // Each case registers the client and the OTHER two, so the only thing that can stand the host down is the
        // guard under test.
        ServiceCollection services = new();
        services.AddSingleton(Client);
        if (missing != "authentication")
        {
            services.AddSingleton(_authentication);
        }

        if (missing != "api-client")
        {
            services.AddSingleton(_apiClient);
        }

        if (missing != "sync-register")
        {
            services.AddSingleton(_sync);
        }

        SocketIs(ClientModels.ConnectionState.Disconnected);
        BackgroundService host = Host(services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }));

        await host.StartAsync(CancellationToken.None);

        (await RanToCompletion(host.ExecuteTask!)).Should()
            .BeTrue("standing down means the run ENDS — a host that fell through to the poll loop hangs here");
        await StopAsync(host);

        AssertNeverConnected();
    }

}
