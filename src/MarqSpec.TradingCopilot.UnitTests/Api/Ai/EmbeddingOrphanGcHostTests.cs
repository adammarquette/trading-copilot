using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The orphan-GC's per-pass work (gh#902): sweep each producer-backed owner kind, best-effort. The actual anti-join
/// DELETE is relational-only (gh#109) and QA integration-tier; what is unit-testable is the orchestration over the
/// <see cref="IEmbeddingOrphanStore"/> seam — which kinds it sweeps, its fault-tolerance, and the total it returns.
/// </summary>
public class EmbeddingOrphanGcHostTests
{
    private readonly IEmbeddingOrphanStore _store = A.Fake<IEmbeddingOrphanStore>();

    private Task<int> SweepAsync() =>
        EmbeddingOrphanGcHost.SweepAsync(_store, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task SweepAsync_ShouldDeleteOrphansForEachSweepableKind_AndReturnTheTotal()
    {
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.SoftSignal, A<CancellationToken>._)).Returns(2);
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._)).Returns(3);

        (await SweepAsync()).Should().Be(5);

        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.SoftSignal, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SweepAsync_ShouldNeverSweepProducerlessKinds()
    {
        await SweepAsync();

        EmbeddingOwnerKind[] producerless =
            [EmbeddingOwnerKind.Unknown, EmbeddingOwnerKind.Suggestion, EmbeddingOwnerKind.Rule, EmbeddingOwnerKind.MarketSnapshot];
        foreach (EmbeddingOwnerKind kind in producerless)
        {
            A.CallTo(() => _store.DeleteOrphansAsync(kind, A<CancellationToken>._)).MustNotHaveHappened();
        }
    }

    [Fact]
    public async Task SweepAsync_ShouldSweepTheOtherKinds_WhenOneKindFaults()
    {
        // Best-effort per kind: a fault sweeping one owner kind is logged and must not prevent the others.
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.SoftSignal, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("delete blew up"));
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._)).Returns(4);

        (await SweepAsync()).Should().Be(4);
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SweepAsync_ShouldRethrow_WhenTheCallerCancels()
    {
        // A genuine caller cancellation is host shutdown, not a per-kind fault to swallow.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        A.CallTo(() => _store.DeleteOrphansAsync(A<EmbeddingOwnerKind>._, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> sweep = () => EmbeddingOrphanGcHost.SweepAsync(_store, NullLogger.Instance, cts.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
    }
}
