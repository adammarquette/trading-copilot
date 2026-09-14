using FakeItEasy;
using MarqSpec.Client.Tradovate.WebSocket;
using MarqSpec.TradingCopilot.Api.MarketData;
using MarqSpec.TradingCopilot.Integration.Tradovate;
using MarqSpec.TradingCopilot.UnitTests.Api.Venues;
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
/// <b>The loop itself is tested by the base class</b>, <see cref="TradovateSocketConnectionHostContract"/> — backoff
/// growth and its reset, the <c>Connecting</c>/<c>Reconnecting</c> wait, the unrecognised-state default,
/// containment, the <see cref="OperationCanceledException"/> retry condition, the stand-down triad and clean exit
/// are all inherited, and the trading host inherits the same ones from the same source (gh#1054). What remains here
/// is this socket's own post-connect obligation: a <b>per-key replay that must survive a partial failure</b>, which
/// is the half that genuinely differs from its sibling's single <c>user/syncrequest</c>.
/// </para>
/// <para>
/// Every test that proves a <b>negative</b> ("it did not connect", "it did not resubscribe") first waits on
/// <see cref="TradovateSocketConnectionHostContract.PassesObserved"/>, so the assertion rests on passes that really
/// ran rather than on a sleep the host might have spent doing nothing at all.
/// </para>
/// </remarks>
public class TradovateMarketDataConnectionHostTests : TradovateSocketConnectionHostContract
{
    private readonly TradovateQuoteSubscriptions _subscriptions = new();

    /// <inheritdoc />
    protected override BackgroundService CreateHost(
        IServiceProvider services,
        TimeSpan pollInterval,
        TimeSpan maxBackoff,
        TimeSpan degradedGrace,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null) =>
        new TradovateMarketDataConnectionHost(
            services,
            NullLogger<TradovateMarketDataConnectionHost>.Instance,
            pollInterval,
            maxBackoff,
            degradedGrace,
            delayAsync);

    /// <inheritdoc />
    protected override void Register(ServiceCollection services)
    {
        services.AddSingleton(Client);
        services.AddSingleton(_subscriptions);
    }

    /// <inheritdoc />
    /// <remarks>The register is the collaborator: without it a reconnect could restore no subscription at all.</remarks>
    protected override void RegisterWithoutARequiredCollaborator(ServiceCollection services) =>
        services.AddSingleton(Client);

    /// <inheritdoc />
    protected override string SocketNameUnderTest => "market-data";

    /// <inheritdoc />
    protected override void ArrangeSocketState(Func<ClientModels.ConnectionState> read) =>
        A.CallTo(() => Client.MarketDataState).ReturnsLazily(read);

    /// <inheritdoc />
    protected override void ArrangeConnect(Func<Task> behaviour) =>
        A.CallTo(() => Client.ConnectMarketDataAsync(A<CancellationToken>._)).ReturnsLazily(behaviour);

    /// <inheritdoc />
    /// <remarks>
    /// One live contract, so exactly one subscribe is owed per pass — the same unit of work the trading host's one
    /// sync is, which is what lets the shared cadence tests read the two sockets identically.
    /// </remarks>
    protected override async Task ArrangePostConnectWorkAsync(Func<Task> behaviour)
    {
        await HoldsQuotesOn("7");
        A.CallTo(() => Client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).ReturnsLazily(behaviour);
    }

    /// <inheritdoc />
    protected override void AssertNeverConnected() =>
        A.CallTo(() => Client.ConnectMarketDataAsync(A<CancellationToken>._)).MustNotHaveHappened();

    /// <inheritdoc />
    protected override void AssertNeverDidPostConnectWork() =>
        A.CallTo(() => Client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();

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
        A.CallTo(() => Client.ConnectMarketDataAsync(A<CancellationToken>._)).Invokes(() => connected.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(connected)).Should().BeTrue();
        await StopAsync(host);
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
        A.CallTo(() => Client.SubscribeQuoteAsync("8", A<CancellationToken>._)).Invokes(() => replayed.TrySetResult());

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(replayed)).Should().BeTrue();
        await StopAsync(host);

        A.CallTo(() => Client.SubscribeQuoteAsync("7", A<CancellationToken>._)).MustHaveHappened();
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
        A.CallTo(() => Client.SubscribeQuoteAsync("7", A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("the socket dropped mid-replay (test)");
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
    public async Task ExecuteAsync_ShouldReplayTheOtherKeys_InTheSamePass_WhenOneSubscriptionFailsMidReplay()
    {
        // A key that fails must not abort the pass. Aborting would let one persistently failing contract starve
        // every key behind it — the same connected-but-silent feed this host exists to prevent, just for the tail
        // of the register.
        //
        // Two properties make this discriminating, and the first version of it had neither.
        //
        // It is ORDER-INDEPENDENT. Which key the replay reaches first is the register's business, not this test's:
        // `_owed` is filled from a ConcurrentDictionary's bucket walk, so the sequence follows string hashes rather
        // than the order these two lines run in — and it is stable, not random, because a small
        // ConcurrentDictionary built with StringComparer.Ordinal uses the non-randomised comparer. Naming a fixed
        // thrower therefore pins the test to that accident, and pinning it to the wrong key makes it VACUOUS: a
        // thrower reached last is only attempted after the survivor already succeeded, which is exactly what the
        // abort-the-whole-pass bug does too. So the fake throws on whichever contract it is handed FIRST.
        //
        // And it asserts SAME-PASS. "The survivor was eventually subscribed" is true of the bug as well — the buggy
        // pass aborts, but `_owed` still carries both keys into the next pass, which then subscribes the survivor.
        // The entire content of the fix is that the survivor does not WAIT for that next pass, so the assertion is
        // that both subscribes happened on one loop pass, counted by the host's own state reads.
        await HoldsQuotesOn("7");
        await HoldsQuotesOn("8");

        string? thrower = null;
        int throwingPass = 0;
        int survivingPass = 0;
        TaskCompletionSource survived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocketIs(ClientModels.ConnectionState.Disconnected, ClientModels.ConnectionState.Connected);
        A.CallTo(() => Client.SubscribeQuoteAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string key, CancellationToken _) =>
            {
                if (Interlocked.CompareExchange(ref thrower, key, null) is null)
                {
                    // The first contract of the first pass, and only ever that one: a later retry of the same key
                    // succeeds, so the host cannot spin on it for the rest of the test.
                    throwingPass = PassesSoFar;
                    throw new InvalidOperationException($"contract {key} fails its replay (test)");
                }

                if (!string.Equals(key, thrower, StringComparison.Ordinal) && survivingPass == 0)
                {
                    survivingPass = PassesSoFar;
                    survived.TrySetResult();
                }

                return Task.CompletedTask;
            });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(survived)).Should().BeTrue("a failing key must not starve the keys behind it");
        await StopAsync(host);

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
        A.CallTo(() => Client.SubscribeQuoteAsync("7", A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("a transient failure on this contract (test)");
            }

            retried.TrySetResult();
            return Task.CompletedTask;
        });

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);

        (await Signalled(retried)).Should().BeTrue();
        await StopAsync(host);

        // The key that already landed is not owed a second subscribe.
        A.CallTo(() => Client.SubscribeQuoteAsync("8", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReplay_WhileTheSocketIsAlreadyConnected()
    {
        // A healthy socket already carries its subscriptions; re-sending them would rest on the gateway treating a
        // duplicate subscribe as harmless. This is the half that differs from the trading host, which DOES act on a
        // connected socket — its sync is owed until a snapshot lands, and "connected" is not evidence one has.
        await HoldsQuotesOn("7");
        SocketIs(ClientModels.ConnectionState.Connected);

        BackgroundService host = Host();
        await host.StartAsync(CancellationToken.None);
        (await PassesObserved(3)).Should().BeTrue("the assertions below only mean something if passes really ran");
        await StopAsync(host);

        AssertNeverConnected();
        AssertNeverDidPostConnectWork();
    }
}
