using FakeItEasy;
using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The orphan-GC's per-pass work (gh#902, gh#915): the orphaned-owner sweep over each producer-backed owner kind,
/// then the crash-leaked stale-model backstop when a current model is known. The actual anti-join DELETEs are
/// relational-only (gh#109) and QA integration-tier; what is unit-testable is the orchestration over the
/// <see cref="IEmbeddingOrphanStore"/> seam — which sweeps run, its fault-tolerance, and the total it returns.
/// </summary>
public class EmbeddingOrphanGcHostTests
{
    private const string CurrentModel = "cohere-embed-v3";

    private readonly IEmbeddingOrphanStore _store = A.Fake<IEmbeddingOrphanStore>();

    private Task<int> SweepAsync(string? currentModel = null) =>
        EmbeddingOrphanGcHost.SweepAsync(_store, currentModel, NullLogger.Instance, CancellationToken.None);

    // --- Orphaned-owner sweep (gh#902) ---

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

        // Suggestion / JournalEntry gained producers in gh#1065 and moved onto the allow-list; Rule is still waiting on
        // the rulebook epic (gh#15), MarketSnapshot on its own producer, and Unknown is the refusable sentinel.
        EmbeddingOwnerKind[] producerless =
            [EmbeddingOwnerKind.Unknown, EmbeddingOwnerKind.Rule, EmbeddingOwnerKind.MarketSnapshot];
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

        Func<Task> sweep = () => EmbeddingOrphanGcHost.SweepAsync(_store, null, NullLogger.Instance, cts.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Crash-leaked stale-model backstop (gh#915) ---

    [Fact]
    public async Task SweepAsync_ShouldRunBothSweepsAndSumTheTotal_WhenACurrentModelIsKnown()
    {
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.SoftSignal, A<CancellationToken>._)).Returns(2);
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._)).Returns(3);
        A.CallTo(() => _store.DeleteStaleModelDuplicatesAsync(CurrentModel, A<CancellationToken>._)).Returns(4);

        (await SweepAsync(CurrentModel)).Should().Be(9);

        A.CallTo(() => _store.DeleteStaleModelDuplicatesAsync(CurrentModel, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SweepAsync_ShouldSkipTheStaleModelBackstop_WhenNoCurrentModelIsConfigured()
    {
        // With no provider (null model) there is no current model, so there is no duplicate to resolve -- the backstop
        // must not run at all rather than sweep against an empty model string.
        await SweepAsync(currentModel: null);

        A.CallTo(() => _store.DeleteStaleModelDuplicatesAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SweepAsync_ShouldStillReturnTheOrphanTotal_WhenTheBackstopFaults()
    {
        // Best-effort: a backstop fault is logged and must not lose the orphaned-owner sweep's work or throw.
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.SoftSignal, A<CancellationToken>._)).Returns(2);
        A.CallTo(() => _store.DeleteOrphansAsync(EmbeddingOwnerKind.Topic, A<CancellationToken>._)).Returns(3);
        A.CallTo(() => _store.DeleteStaleModelDuplicatesAsync(CurrentModel, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("backstop delete blew up"));

        (await SweepAsync(CurrentModel)).Should().Be(5);
    }

    [Fact]
    public async Task SweepAsync_ShouldRethrow_WhenTheBackstopCancels()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        A.CallTo(() => _store.DeleteStaleModelDuplicatesAsync(CurrentModel, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> sweep = () => EmbeddingOrphanGcHost.SweepAsync(_store, CurrentModel, NullLogger.Instance, cts.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
    }
}
