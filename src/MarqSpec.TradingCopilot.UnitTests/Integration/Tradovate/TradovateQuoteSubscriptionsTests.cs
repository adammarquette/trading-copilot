using MarqSpec.TradingCopilot.Integration.Tradovate;

namespace MarqSpec.TradingCopilot.UnitTests.Integration.Tradovate;

/// <summary>
/// The register of live Tradovate quote subscriptions (gh#977) — both the memory a reconnect replays from and the
/// single serializer of quote subscribe / unsubscribe traffic on the shared market-data socket.
/// </summary>
/// <remarks>
/// It exists because a <b>host-driven</b> reconnect does <i>not</i> replay subscriptions — the client replays only
/// on its own internal reconnect — so something must remember what was subscribed, or a recovered socket comes back
/// silent and a hidden stop's promotion stalls with no error anywhere. It owns the wire calls, rather than merely
/// counting, because deciding "am I the last holder?" and sending the unsubscribe are two steps with a network round
/// trip between them: a second consumer claiming the same contract in that window would have its brand-new feed
/// killed by the first one's late unsubscribe, and neither replay path would restore it.
/// </remarks>
public class TradovateQuoteSubscriptionsTests
{
    private readonly TradovateQuoteSubscriptions _subscriptions = new();

    private static Func<CancellationToken, Task> Records(List<string> log, string what) =>
        _ =>
        {
            lock (log)
            {
                log.Add(what);
            }

            return Task.CompletedTask;
        };

    [Fact]
    public void LiveKeys_ShouldBeEmpty_BeforeAnythingSubscribes()
    {
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task AcquireAsync_ShouldSubscribeTheWire_AndExposeTheKeyForReplay()
    {
        bool subscribed = false;

        await _subscriptions.AcquireAsync("7", _ => { subscribed = true; return Task.CompletedTask; });

        subscribed.Should().BeTrue();
        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public async Task AcquireAsync_ShouldRegisterTheClaim_BeforeItSubscribesTheWire()
    {
        // Load-bearing ordering, and the reason it is asserted rather than left to a comment: a reconnect landing
        // between the two must still find the contract on the register, or the stream about to start comes back
        // silent. Inverting the two lines leaves every other test in this file green.
        IReadOnlyList<string> liveDuringSubscribe = [];

        await _subscriptions.AcquireAsync("7", _ =>
        {
            liveDuringSubscribe = _subscriptions.LiveKeys;
            return Task.CompletedTask;
        });

        liveDuringSubscribe.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public async Task AcquireAsync_ShouldNotSubscribeTwice_ForASecondHolderOfTheSameContract()
    {
        // One socket, one subscription per contract.
        int subscribes = 0;

        await _subscriptions.AcquireAsync("7", _ => { subscribes++; return Task.CompletedTask; });
        await _subscriptions.AcquireAsync("7", _ => { subscribes++; return Task.CompletedTask; });

        subscribes.Should().Be(1);
    }

    [Fact]
    public async Task AcquireAsync_ShouldRollBackTheClaim_WhenTheWireSubscribeThrows()
    {
        // A claim the caller never got must not survive: it would keep the key on the register (replayed forever
        // for a stream that does not exist) and let the caller's teardown decrement somebody else's count.
        Func<Task> acquire = () => _subscriptions.AcquireAsync(
            "7", _ => throw new InvalidOperationException("the socket refused the subscribe (test)"));

        await acquire.Should().ThrowAsync<InvalidOperationException>();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldUnsubscribeTheWire_ForTheLastHolder()
    {
        bool unsubscribed = false;
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);

        bool wasLast = await _subscriptions.ReleaseAsync("7", () => { unsubscribed = true; return Task.CompletedTask; });

        wasLast.Should().BeTrue();
        unsubscribed.Should().BeTrue();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldKeepTheWireSubscribed_WhileAnotherStreamStillHoldsIt()
    {
        // Unsubscribing on the FIRST teardown would starve the second consumer, which still believes it is streaming.
        bool unsubscribed = false;
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);

        bool wasLast = await _subscriptions.ReleaseAsync("7", () => { unsubscribed = true; return Task.CompletedTask; });

        wasLast.Should().BeFalse();
        unsubscribed.Should().BeFalse();
        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");

        (await _subscriptions.ReleaseAsync("7", () => { unsubscribed = true; return Task.CompletedTask; }))
            .Should().BeTrue();
        unsubscribed.Should().BeTrue();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldNotUnsubscribe_ForAKeyThatWasNeverAcquired()
    {
        // An unbalanced release must not drive the count negative, which would make a later real release look like
        // "not the last holder" and leave the wire subscribed forever.
        bool unsubscribed = false;

        bool wasLast = await _subscriptions.ReleaseAsync("7", () => { unsubscribed = true; return Task.CompletedTask; });

        wasLast.Should().BeFalse();
        unsubscribed.Should().BeFalse();
        _subscriptions.LiveKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldNotDriveTheCountNegative_WhenReleasedMoreOftenThanAcquired()
    {
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);
        (await _subscriptions.ReleaseAsync("7", () => Task.CompletedTask)).Should().BeTrue();
        (await _subscriptions.ReleaseAsync("7", () => Task.CompletedTask)).Should().BeFalse();

        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);

        (await _subscriptions.ReleaseAsync("7", () => Task.CompletedTask))
            .Should().BeTrue("the count restarted at one, not below zero");
    }

    [Fact]
    public async Task AcquireAsync_ShouldNotInterleaveWithAConcurrentRelease_OnTheSameContract()
    {
        // THE race the register exists to close. A last-holder release decides it may unsubscribe, then awaits the
        // wire call; if a newcomer could claim and subscribe the same contract inside that window, the release's
        // late unsubscribe would kill the newcomer's brand-new feed -- and nothing would restore it, because the
        // client forgets the key before sending and the socket never leaves Connected.
        List<string> log = [];
        TaskCompletionSource unsubscribeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishUnsubscribe = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await _subscriptions.AcquireAsync("7", Records(log, "subscribe(first)"));

        Task release = _subscriptions.ReleaseAsync("7", async () =>
        {
            lock (log)
            {
                log.Add("unsubscribe");
            }

            unsubscribeStarted.TrySetResult();
            await finishUnsubscribe.Task;
        });

        await unsubscribeStarted.Task; // the unsubscribe is now in flight, holding the gate

        Task newcomer = _subscriptions.AcquireAsync("7", Records(log, "subscribe(newcomer)"));
        newcomer.IsCompleted.Should().BeFalse("the newcomer must wait behind the in-flight unsubscribe");

        finishUnsubscribe.TrySetResult();
        await release;
        await newcomer;

        log.Should().Equal("subscribe(first)", "unsubscribe", "subscribe(newcomer)");
        _subscriptions.LiveKeys.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public async Task ResubscribeAsync_ShouldReplayAKeyThatIsStillHeld()
    {
        bool replayed = false;
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);

        bool sent = await _subscriptions.ResubscribeAsync("7", _ => { replayed = true; return Task.CompletedTask; });

        sent.Should().BeTrue();
        replayed.Should().BeTrue();
    }

    [Fact]
    public async Task ResubscribeAsync_ShouldSkipAKeyNobodyHoldsAnyMore()
    {
        // Its stream ended while the socket was down; replaying it would feed a channel with no reader for the rest
        // of the session.
        bool replayed = false;

        bool sent = await _subscriptions.ResubscribeAsync("7", _ => { replayed = true; return Task.CompletedTask; });

        sent.Should().BeFalse();
        replayed.Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_ShouldDistinguishKeys()
    {
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);
        await _subscriptions.AcquireAsync("8", _ => Task.CompletedTask);

        _subscriptions.LiveKeys.Should().BeEquivalentTo(["7", "8"]);
    }

    [Fact]
    public async Task LiveKeys_ShouldBeASnapshot_SoAReplayIsNotDisturbedByConcurrentTracking()
    {
        // The host enumerates LiveKeys while streams start and stop on other threads.
        await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);
        IReadOnlyList<string> snapshot = _subscriptions.LiveKeys;

        await _subscriptions.AcquireAsync("8", _ => Task.CompletedTask);
        await _subscriptions.ReleaseAsync("7", () => Task.CompletedTask);

        snapshot.Should().ContainSingle().Which.Should().Be("7");
    }

    [Fact]
    public async Task AcquireAndRelease_ShouldBalance_UnderConcurrentUse()
    {
        await Task.WhenAll(Enumerable.Range(0, 256).Select(async _ =>
        {
            await _subscriptions.AcquireAsync("7", _ => Task.CompletedTask);
            await _subscriptions.ReleaseAsync("7", () => Task.CompletedTask);
        }));

        _subscriptions.LiveKeys.Should().BeEmpty();
    }
}
