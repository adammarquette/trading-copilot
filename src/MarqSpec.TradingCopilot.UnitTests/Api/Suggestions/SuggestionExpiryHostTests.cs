using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Suggestions;

/// <summary>
/// The expire sweep's per-pass work (gh#545 / gh#718): expire the due suggestions, then push one per-owner realtime
/// signal for each. The behaviours that matter — a signal per voided suggestion routed to its own owner, the count
/// returned for logging, and a push that can never fail or unwind the (already-committed) expire write.
/// </summary>
public class SuggestionExpiryHostTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    private readonly ISuggestionExpiry _expiry = A.Fake<ISuggestionExpiry>();
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();

    private Task<int> RunAsync() =>
        SuggestionExpiryHost.ExpireAndNotifyAsync(
            _expiry, _notifier, NullLogger.Instance, _now, CancellationToken.None);

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldPushEachExpiredSuggestionToItsOwner_AndReturnTheCount()
    {
        // The expire transition is set-based, so it recovers the (SuggestionId, UserId) it voided and this pass pushes
        // one per-owner ExpiredVoid signal (Clients.User, R-20 -- never a broadcast). Two owners' suggestions expire;
        // each gets exactly their own, and the count is returned for the sweep log.
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        A.CallTo(() => _expiry.ExpireDueAsync(_now, A<CancellationToken>._))
            .Returns<IReadOnlyList<SuggestionTransition>>(
                [new SuggestionTransition(mine, ownerA), new SuggestionTransition(theirs, ownerB)]);

        int expired = await RunAsync();

        expired.Should().Be(2);
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                ownerA,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == mine && push.State == "ExpiredVoid" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                ownerB,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == theirs && push.State == "ExpiredVoid" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldNotPush_WhenNothingExpired()
    {
        A.CallTo(() => _expiry.ExpireDueAsync(_now, A<CancellationToken>._))
            .Returns<IReadOnlyList<SuggestionTransition>>([]);

        (await RunAsync()).Should().Be(0);
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldReturnTheCount_WhenAPushFails()
    {
        // The push is best-effort and AFTER the commit (the void is durable inside ExpireDueAsync): a hub fault only
        // logs and must never fail the pass. The count is still returned, and nothing throws out of the sweep.
        A.CallTo(() => _expiry.ExpireDueAsync(_now, A<CancellationToken>._))
            .Returns<IReadOnlyList<SuggestionTransition>>(
                [new SuggestionTransition(Guid.NewGuid(), Guid.NewGuid())]);
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        (await RunAsync()).Should().Be(1);
    }
}
