using FakeItEasy;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ClientModels = MarqSpec.Client.Tradovate.Api.Models;

namespace MarqSpec.TradingCopilot.UnitTests.Api.MarketData;

/// <summary>
/// The Tradovate market-data connection host (gh#977): the owner of the process-wide market-data socket's
/// lifecycle — connect, reconnect, <b>resubscribe</b>.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the client's own recovery leaves two gaps the adapter must not inherit. First, the client's
/// internal reconnect <b>gives up after a single failed attempt</b> and parks the socket in
/// <see cref="ClientModels.ConnectionState.Disconnected"/> — a brief network blip would otherwise end quotes for
/// the rest of the session. Second, the <b>manual</b> connect path — the only way back from
/// <c>Disconnected</c> — reconnects <i>without</i> replaying subscriptions, so a socket recovered that way comes
/// back live-but-silent. A silent quote feed is a safety concern, not a cosmetic one: it is what stalls a hidden
/// stop's promotion, with no exception raised anywhere.
/// </para>
/// <para>
/// Everything is caught: an escape from <c>ExecuteAsync</c> stops the whole application under the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>, which would take the auto-flatten watchdog and the kill
/// switch down with it.
/// </para>
/// <para>
/// Every test that proves a <b>negative</b> ("it did not connect", "it did not resubscribe") first waits on
/// <see cref="PassesObserved"/>, so the assertion rests on passes that really ran rather than on a sleep the host
/// might have spent doing nothing at all.
/// </para>
/// </remarks>
public class TradovateMarketDataConnectionHostTests
{
    private static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    private readonly ITradovateWebSocketClient _client = A.Fake<ITradovateWebSocketClient>();
    private readonly TradovateQuoteSubscriptions _subscriptions = new();
    private readonly TaskCompletionSource _witness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stateReads;
    private int _witnessAt = int.MaxValue;

    private TradovateMarketDataConnectionHost Host(IServiceProvider? services = null) =>
        new(services ?? Registered(),
            NullLogger<TradovateMarketDataConnectionHost>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxBackoff: TimeSpan.FromMilliseconds(2));

    private IServiceProvider Registered()
    {
        ServiceCollection services = new();
        services.AddSingleton(_client);
        services.AddSingleton(_subscriptions);
        return services.BuildServiceProvider();
    }

    // Each state read is one loop pass, so counting them is the host's own heartbeat: it lets a "did not happen"
    // assertion say "across N real passes" rather than "within some wall-clock window".
    private void SocketIs(params ClientModels.ConnectionState[] states)
    {
        A.CallTo(() => _client.MarketDataState).ReturnsLazily(() =>
        {
            int read = Interlocked.Increment(ref _stateReads);
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

    // A stream holding quotes on a contract, without putting anything on the fake's wire: what the host replays is
    // then unambiguously the host's own doing.
    private Task HoldsQuotesOn(string key) => _subscriptions.AcquireAsync(key, _ => Task.CompletedTask);

    [Fact]
    public async Task ExecuteAsync_ShouldConnectTheMarketDataSocket_WhenItIsDisconnected()
    {
        // The client never opens the socket on its own, and every read path (bars, quotes) refuses while it is
        // down — so somebody has to open it. That is this host, and only this host.
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        A.CallTo(() => _client.ConnectMarketDataAsync(A<CancellationToken>._)).Invokes(() => connected.TrySetResult());

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(connected)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReplayEveryLiveQuoteSubscription_AfterAHostDrivenConnect()
    {
        // THE point of the host. ConnectMarketDataAsync takes the client's non-replaying path, so a socket brought
        // back that way is subscribed to nothing: the stream stays open and simply never ticks again.
        await HoldsQuotesOn("7");
        await HoldsQuotesOn("8");

        TaskCompletionSource replayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => _client.SubscribeQuoteAsync("8", A<CancellationToken>._)).Invokes(() => replayed.TrySetResult());

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(replayed)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.SubscribeQuoteAsync("7", A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryTheReplay_WhenResubscribingFails_SoAConnectedSocketIsNeverLeftSilent()
    {
        // A replay that half-succeeded leaves a CONNECTED socket carrying no subscription — a state no later pass
        // would revisit, because "connected" looks healthy. The pending replay must survive into the next pass.
        await HoldsQuotesOn("7");

        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => _client.SubscribeQuoteAsync("7", A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("the socket dropped mid-replay (test)");
            }

            retried.TrySetResult();
            return Task.CompletedTask;
        });

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReplayTheOtherKeys_InTheSamePass_WhenOneSubscriptionFailsMidReplay()
    {
        // A key that fails must not abort the pass. Aborting would let one persistently failing contract starve
        // every key behind it — the same connected-but-silent feed this host exists to prevent, just for the tail
        // of the register.
        //
        // Two properties make this discriminating, and the first version of it had neither.
        //
        // It is ORDER-INDEPENDENT. Which key the replay reaches first is the register's business, not this test's:
        // `owed` is filled from a ConcurrentDictionary's bucket walk, so the sequence follows string hashes rather
        // than the order these two lines run in — and it is stable, not random, because a small
        // ConcurrentDictionary built with StringComparer.Ordinal uses the non-randomised comparer. Naming a fixed
        // thrower therefore pins the test to that accident, and pinning it to the wrong key makes it VACUOUS: a
        // thrower reached last is only attempted after the survivor already succeeded, which is exactly what the
        // abort-the-whole-pass bug does too. So the fake throws on whichever contract it is handed FIRST.
        //
        // And it asserts SAME-PASS. "The survivor was eventually subscribed" is true of the bug as well — the buggy
        // pass aborts, but `owed` still carries both keys into the next pass, which then subscribes the survivor.
        // The entire content of the fix is that the survivor does not WAIT for that next pass, so the assertion is
        // that both subscribes happened on one loop pass, counted by the host's own state reads.
        await HoldsQuotesOn("7");
        await HoldsQuotesOn("8");

        string? thrower = null;
        int throwingPass = 0;
        int survivingPass = 0;
        TaskCompletionSource survived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => _client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string key, CancellationToken _) =>
            {
                if (Interlocked.CompareExchange(ref thrower, key, null) is null)
                {
                    // The first contract of the first pass, and only ever that one: a later retry of the same key
                    // succeeds, so the host cannot spin on it for the rest of the test.
                    throwingPass = Volatile.Read(ref _stateReads);
                    throw new InvalidOperationException($"contract {key} fails its replay (test)");
                }

                if (!string.Equals(key, thrower, StringComparison.Ordinal) && survivingPass == 0)
                {
                    survivingPass = Volatile.Read(ref _stateReads);
                    survived.TrySetResult();
                }

                return Task.CompletedTask;
            });

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(survived)).Should().BeTrue("a failing key must not starve the keys behind it");
        await host.StopAsync(CancellationToken.None);

        thrower.Should().NotBeNull();
        survivingPass.Should().Be(
            throwingPass,
            "the surviving key must be subscribed on the SAME pass the other one failed, not deferred to the next");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryOnlyTheFailedKey_NotTheOnesAlreadyResubscribed()
    {
        // Re-sending a key that already landed would rest on the gateway treating a duplicate subscribe as a
        // harmless no-op — something this side cannot know. Only what is still owed is retried.
        await HoldsQuotesOn("7");
        await HoldsQuotesOn("8");

        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => _client.SubscribeQuoteAsync("7", A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("a transient failure on this contract (test)");
            }

            retried.TrySetResult();
            return Task.CompletedTask;
        });

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        // The key that already landed is not owed a second subscribe.
        A.CallTo(() => _client.SubscribeQuoteAsync("8", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(ClientModels.ConnectionState.Connecting)]
    [InlineData(ClientModels.ConnectionState.Reconnecting)]
    public async Task ExecuteAsync_ShouldNotConnect_WhileTheClientIsAlreadyAttemptingIt(ClientModels.ConnectionState state)
    {
        // The client's own reconnect DOES replay subscriptions. Racing it with a manual connect would tear the
        // transport down mid-attempt and land on the non-replaying path instead — turning a self-healing drop into
        // a silent feed. An in-progress state therefore means "wait", exactly as it reads as "down" for liveness.
        await HoldsQuotesOn("7");
        SocketIs(state);

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNeitherConnectNorReplay_WhileTheSocketIsAlreadyConnected()
    {
        // A healthy socket already carries its subscriptions; re-driving connect would drop them.
        await HoldsQuotesOn("7");
        SocketIs(ClientModels.ConnectionState.Connected);

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await host.StopAsync(CancellationToken.None);

        A.CallTo(() => _client.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRetrying_WhenTheConnectAttemptFails()
    {
        // The gap the client leaves: its internal reconnect gives up after one failure. An outage lasting longer
        // than a single attempt must still recover on its own.
        int attempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        A.CallTo(() => _client.ConnectMarketDataAsync(A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                retried.TrySetResult();
            }

            throw new InvalidOperationException("the venue is unreachable (test)");
        });

        TradovateMarketDataConnectionHost host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await host.StopAsync(CancellationToken.None);

        host.ExecuteTask!.IsFaulted.Should().BeFalse("a venue outage must never stop the application");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenTheTradovateClientIsNotRegistered()
    {
        // Tradovate is not wired in every deployment. An unconfigured venue is an idle host, not a startup failure.
        TradovateMarketDataConnectionHost host = Host(new ServiceCollection().BuildServiceProvider());

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
        TradovateMarketDataConnectionHost host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopQuietly_WhenTheSubscriptionRegisterIsNotRegistered()
    {
        // A wiring defect: the client is present but the register is not. Replaying from a register nothing writes
        // to would silently resubscribe nothing, so refuse to drive the socket rather than pretend to guard it.
        ServiceCollection services = new();
        services.AddSingleton(_client);
        SocketIs(ClientModels.ConnectionState.Disconnected);
        TradovateMarketDataConnectionHost host = Host(services.BuildServiceProvider());

        await host.StartAsync(CancellationToken.None);
        Func<Task> run = () => host.ExecuteTask!;

        await run.Should().NotThrowAsync();
        await host.StopAsync(CancellationToken.None);
        A.CallTo(() => _client.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldExitCleanly_OnShutdown()
    {
        SocketIs(ClientModels.ConnectionState.Connected);
        TradovateMarketDataConnectionHost host = Host();

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
