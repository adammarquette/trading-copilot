using FakeItEasy;
using MarqSpec.Client.Tradovate.Authentication;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.Accounts;
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
/// The grace pass is load-bearing in the other direction. When the <i>client</i> drove the reconnect it sends the
/// sync itself, immediately after it reports <see cref="ClientModels.ConnectionState.Connected"/> — so a host that
/// synced the moment it saw Connected would put a duplicate <c>user/syncrequest</c> on the ordinary path, and what
/// Tradovate does with a second one is not pinned anywhere on this side.
/// </para>
/// <para>
/// Everything is caught: an escape from <c>ExecuteAsync</c> stops the whole application under the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>, taking the auto-flatten watchdog and the kill switch with it.
/// </para>
/// <para>
/// Every test that proves a <b>negative</b> ("it did not connect", "it did not sync") first waits on
/// <see cref="PassesObserved"/>, so the assertion rests on passes that really ran rather than on a sleep the host
/// might have spent doing nothing at all. Every wait is time-bound — a bare <see cref="TaskCompletionSource"/> await
/// that never completes hangs the whole run and reads as slow CI rather than as a red test.
/// </para>
/// </remarks>
public class TradovateTradingConnectionHostTests
{
    private static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    private const long UserId = 42;

    private readonly ITradovateWebSocketClient _client = A.Fake<ITradovateWebSocketClient>();
    private readonly IAuthenticationService _authentication = A.Fake<IAuthenticationService>();
    private readonly TaskCompletionSource _witness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stateReads;
    private int _witnessAt = int.MaxValue;

    public TradovateTradingConnectionHostTests()
    {
        A.CallTo(() => _authentication.GetUserIdAsync(A<CancellationToken>._)).Returns<long?>(UserId);
    }

    private TradovateTradingConnectionHost Host(IServiceProvider? services = null) =>
        new(services ?? Registered(),
            NullLogger<TradovateTradingConnectionHost>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxBackoff: TimeSpan.FromMilliseconds(2));

    private IServiceProvider Registered()
    {
        ServiceCollection services = new();
        services.AddSingleton(_client);
        services.AddSingleton(_authentication);
        return services.BuildServiceProvider();
    }

    // Each state read is one loop pass, so counting them is the host's own heartbeat: it lets a "did not happen"
    // assertion say "across N real passes" rather than "within some wall-clock window", and it lets a test act
    // (below) at an exact point in the host's cycle instead of racing it with a sleep.
    private void SocketIs(params ClientModels.ConnectionState[] states) => SocketIs(_ => { }, states);

    private void SocketIs(Action<int> onRead, params ClientModels.ConnectionState[] states)
    {
        A.CallTo(() => _client.TradingState).ReturnsLazily(() =>
        {
            int read = Interlocked.Increment(ref _stateReads);
            onRead(read);
            if (read >= Volatile.Read(ref _witnessAt))
            {
                _witness.TrySetResult();
            }

            return states[Math.Min(read - 1, states.Length - 1)];
        });
    }

    private Task<bool> PassesObserved(int passes)
    {
        Volatile.Write(ref _witnessAt, passes);
        return Signalled(_witness);
    }

    private static async Task<bool> Signalled(TaskCompletionSource signal)
    {
        return await Task.WhenAny(signal.Task, Task.Delay(Timeout)) == signal.Task;
    }

    // The real client raises SyncCompleted from inside SyncRequestAsync, before it returns. Mirroring that on the
    // fake is what makes "the host stops asking once a snapshot has landed" a property of the host rather than of
    // this test's setup.
    private void SyncRaisesCompletion(Action? onSync = null)
    {
        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                onSync?.Invoke();
                RaiseSyncCompleted();
                return Task.FromResult(new ClientModels.SyncResult());
            });
    }

    private void RaiseSyncCompleted() =>
        _client.SyncCompleted += Raise.With(_client, new ClientModels.SyncResult());

    [Fact]
    public async Task ExecuteAsync_ShouldConnectTheTradingSocket_WhenItIsDisconnected()
    {
        // The client never opens the socket on its own, and its internal reconnect only ever runs after a socket
        // that was up went down — so at startup somebody has to open it, and that is this host and only this host.
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._)).Invokes(() => connected.TrySetResult());
        SyncRaisesCompletion();

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(connected)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendTheSyncRequest_OnTheSamePassAsAHostDrivenConnect()
    {
        // THE point of the host. ConnectTradingAsync authorizes the socket and stops there — the client sends
        // user/syncrequest only from its OWN reconnect path — and Tradovate pushes props entity frames only to a
        // socket that has synced. So a host-driven connect that did not sync leaves the socket connected and
        // permanently silent: no order, fill or position event, and nothing raised.
        //
        // It is asserted SAME-PASS, not "eventually". A host that deferred the sync to a later pass would be
        // indistinguishable here from one that syncs at once unless the pass is pinned — and deferring is exactly
        // the shape of the bug, because the socket looks healthy in the meantime.
        int connectPass = 0;
        int syncPass = 0;
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._))
            .Invokes(() => connectPass = Volatile.Read(ref _stateReads));
        SyncRaisesCompletion(() =>
        {
            syncPass = Volatile.Read(ref _stateReads);
            synced.TrySetResult();
        });

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue("a connect the host drove is never synced by anything else");
        await host.StopAsync(CancellationToken.None);

        syncPass.Should().Be(connectPass, "the socket is silent for every pass between the connect and the sync");
        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
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

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.SyncRequestAsync(
                A<ClientModels.SyncRequest>.That.Matches(request => request.Users.SequenceEqual(new[] { UserId })),
                A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSendTheSyncRequest_WhenTheSocketIsFoundConnectedAndNothingSyncedIt()
    {
        // The gap the client's own reconnect can still leave without failing: it skips the sync entirely when the
        // authenticated user id is unavailable, and it reports Connected either way. A socket that is up and has
        // never synced is the silent one, so "connected" alone is not evidence the feed is alive.
        TaskCompletionSource synced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion(() => synced.TrySetResult());

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(synced)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        // A connected socket must never be torn down just to sync it: the manual connect rebuilds the transport.
        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._)).MustNotHaveHappened();
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
        SocketIs(read => { if (read == 2) { RaiseSyncCompleted(); } }, ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion();

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(6)).Should().BeTrue("the assertion below only means something if passes really ran");
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopSendingSyncRequests_OnceOneHasCompleted()
    {
        // A sync is a snapshot of every order, fill and position the user owns. Re-sending it every pass would
        // re-deliver all of it to every consumer for the life of the process, so the need must be cleared by the
        // snapshot landing rather than by the socket looking healthy.
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion();

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(8)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
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
        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
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

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        host.ExecuteTask!.IsFaulted.Should().BeFalse("a venue fault must never stop the application");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_AndNotSync_WhenTheAuthenticatedUserIdIsUnavailable()
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

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(asked)).Should().BeTrue("an unavailable user id is retried, not settled into");
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        host.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Theory]
    [InlineData(ClientModels.ConnectionState.Connecting)]
    [InlineData(ClientModels.ConnectionState.Reconnecting)]
    public async Task ExecuteAsync_ShouldNeitherConnectNorSync_WhileTheClientIsAlreadyAttemptingIt(
        ClientModels.ConnectionState state)
    {
        // The client's own reconnect DOES send the sync. Racing it with a manual connect would tear the transport
        // down mid-attempt and land on the path that does not sync — turning a self-healing drop into a silent
        // socket. And a sync sent over a transport that is not up yet just throws.
        SocketIs(state);

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
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
        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                retried.TrySetResult();
            }

            throw new InvalidOperationException("the venue is unreachable (test)");
        });

        TradovateTradingConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        // A sync sent over a socket that failed to connect can only throw.
        A.CallTo(() => _client.SyncRequestAsync(A<ClientModels.SyncRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        host.ExecuteTask!.IsFaulted.Should().BeFalse("a venue outage must never stop the application");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenTheTradovateClientIsNotRegistered()
    {
        // Tradovate is not wired in every deployment. An unconfigured venue is an idle host, not a startup failure.
        TradovateTradingConnectionHost host = Host(new ServiceCollection().BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenBuildingTheClientThrows()
    {
        // Missing or malformed Tradovate credentials throw while the client is constructed. Under the default
        // StopHost behaviour that would stop the platform — trading, auto-flatten and kill switch included.
        ServiceCollection services = new();
        services.AddSingleton<ITradovateWebSocketClient>(
            _ => throw new InvalidOperationException("Tradovate credentials are not configured (test)."));
        TradovateTradingConnectionHost host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotDriveTheSocket_WhenTheAuthenticationServiceIsNotRegistered()
    {
        // A wiring defect: the client is present but the service that names the authenticated user is not. Connecting
        // anyway would produce the exact failure this host exists to prevent — an authorized socket that can never be
        // synced, and so never delivers an order event — so stand down loudly instead of half-driving it.
        ServiceCollection services = new();
        services.AddSingleton(_client);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        TradovateTradingConnectionHost host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
        await host.StopAsync(CancellationToken.None);
        A.CallTo(() => _client.ConnectTradingAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldExitCleanly_OnShutdown()
    {
        SocketIs(ClientModels.ConnectionState.Connected);
        SyncRaisesCompletion();
        TradovateTradingConnectionHost host = Host();

        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(2)).Should().BeTrue();

        // StopAsync returning at all is half the assertion: it awaits the run, so a loop that ignored the stopping
        // token would hang here rather than fail. The other half is that the run ended without a FAULT — a fault
        // escaping a BackgroundService stops the whole application under the default StopHost behaviour.
        await host.StopAsync(CancellationToken.None);

        host.ExecuteTask!.IsCompleted.Should().BeTrue();
        host.ExecuteTask!.IsFaulted.Should().BeFalse("a shutdown is a clean stop, not a fault");
    }
}
