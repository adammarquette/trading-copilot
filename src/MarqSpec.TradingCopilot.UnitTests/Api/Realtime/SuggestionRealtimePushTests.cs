using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Realtime;

/// <summary>
/// The single owner of the best-effort per-owner push policy shared by the drift watcher and the expire sweep
/// (gh#718): one signal per transitioned row to its owner, a hub fault swallowed so it never stops the remaining
/// rows or fails the (already-committed) pass, and a caller cancellation propagated as the clean stop it is.
/// </summary>
public class SuggestionRealtimePushTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();

    [Fact]
    public async Task PushTransitionsSafelyAsync_ShouldPushOneSignalPerRow_ToItsOwnerWithTheGivenState()
    {
        Guid oneId = Guid.NewGuid();
        Guid oneOwner = Guid.NewGuid();
        Guid twoId = Guid.NewGuid();
        Guid twoOwner = Guid.NewGuid();

        await _notifier.PushTransitionsSafelyAsync(
            [new SuggestionTransition(oneId, oneOwner), new SuggestionTransition(twoId, twoOwner)],
            SuggestionState.Stale, _now, NullLogger.Instance, CancellationToken.None);

        A.CallTo(() => _notifier.SuggestionChangedAsync(
                oneOwner,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == oneId && push.State == "Stale" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                twoOwner,
                A<RealtimeSuggestion>.That.Matches(push => push.SuggestionId == twoId && push.State == "Stale" && push.At == _now),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PushTransitionsSafelyAsync_ShouldSwallowAFault_AndStillPushTheRemainingRows()
    {
        // A hub fault on one row must neither propagate (the transition is already durable) nor abandon the rest.
        Guid failOwner = Guid.NewGuid();
        Guid okOwner = Guid.NewGuid();
        A.CallTo(() => _notifier.SuggestionChangedAsync(failOwner, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        Func<Task> push = () => _notifier.PushTransitionsSafelyAsync(
            [new SuggestionTransition(Guid.NewGuid(), failOwner), new SuggestionTransition(Guid.NewGuid(), okOwner)],
            SuggestionState.ExpiredVoid, _now, NullLogger.Instance, CancellationToken.None);

        await push.Should().NotThrowAsync();
        // A fault on an earlier row must not abandon the remaining pushes.
        A.CallTo(() => _notifier.SuggestionChangedAsync(okOwner, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PushTransitionsSafelyAsync_ShouldPropagateCallerCancellation()
    {
        // A caller cancellation is a clean stop, not a delivery fault -- it must propagate, never be swallowed as one.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> push = () => _notifier.PushTransitionsSafelyAsync(
            [new SuggestionTransition(Guid.NewGuid(), Guid.NewGuid())],
            SuggestionState.Stale, _now, NullLogger.Instance, cancelled.Token);

        await push.Should().ThrowAsync<OperationCanceledException>();
    }
}
