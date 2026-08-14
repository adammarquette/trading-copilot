using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Realtime;
using MarqSpec.TradingCopilot.Api.Suggestions;
using MarqSpec.TradingCopilot.Domain.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Suggestions;

/// <summary>
/// The expire sweep's push (gh#718): once <see cref="ISuggestionExpiry.ExpireDueAsync"/> voids past-window
/// suggestions and returns the transitioned rows, each is pushed to its owner (<c>Clients.User</c>, R-20) so a dead
/// card clears without a poll. Exercised through <see cref="SuggestionExpiryHost.ExpireAndNotifyAsync"/> — the seam
/// extracted from the host loop so the recovery-and-route logic can be unit-tested against fakes rather than only by
/// spinning the background host (#824 review). The concrete's real <c>ExecuteUpdate</c> + fingerprint recovery is
/// container-tier (QA, gh#552).
/// </summary>
public class SuggestionExpiryHostTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

    private readonly ISuggestionExpiry _expiry = A.Fake<ISuggestionExpiry>();
    private readonly ISuggestionRealtimeNotifier _notifier = A.Fake<ISuggestionRealtimeNotifier>();

    private Task<int> RunAsync() =>
        SuggestionExpiryHost.ExpireAndNotifyAsync(_expiry, _notifier, _now, NullLogger.Instance, CancellationToken.None);

    private void Expires(params SuggestionTransition[] transitions) =>
        A.CallTo(() => _expiry.ExpireDueAsync(_now, A<CancellationToken>._))
            .Returns(transitions);

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldPushEachVoidedSuggestion_ToItsOwner()
    {
        // gh#718: the expire write committed, so each voided row is pushed to ITS OWN owner (R-20) -- id + the new
        // ExpiredVoid state + when -- the expiry half of the gh#684 realtime push. Two owners: each gets only theirs.
        Guid mine = Guid.NewGuid();
        Guid ownerA = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        Expires(new SuggestionTransition(mine, ownerA), new SuggestionTransition(theirs, ownerB));

        int voided = await RunAsync();

        voided.Should().Be(2);
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                ownerA, new RealtimeSuggestion(mine, nameof(SuggestionState.ExpiredVoid), _now), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _notifier.SuggestionChangedAsync(
                ownerB, new RealtimeSuggestion(theirs, nameof(SuggestionState.ExpiredVoid), _now), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldNotPush_WhenNothingExpired()
    {
        Expires();

        int voided = await RunAsync();

        voided.Should().Be(0);
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExpireAndNotifyAsync_ShouldStillReturnTheCount_WhenTheHubPushThrows()
    {
        // Best-effort (ADR-0021): a hub fault must never fail the sweep. The expire write already committed, so the
        // count is still returned and the fault is swallowed -- the card clears on the next REST read instead.
        Expires(new SuggestionTransition(Guid.NewGuid(), Guid.NewGuid()));
        A.CallTo(() => _notifier.SuggestionChangedAsync(A<Guid>._, A<RealtimeSuggestion>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("hub unreachable"));

        int voided = await RunAsync();

        voided.Should().Be(1, "a hub fault is swallowed -- the committed sweep and its count are unaffected");
    }
}
