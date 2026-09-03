using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

public class EmbeddingOrphanSweepTests
{
    [Fact]
    public void SweepableKinds_ShouldBeExactlyTheProducerBackedKinds()
    {
        // The sweep iterates exactly this list, so it IS the allow-list: SoftSignal (checked against News), Topic
        // (against NewsTopics), and since gh#1065 Suggestion (against Suggestions) and JournalEntry (against Trades)
        // -- the only kinds with a producer to prove an owner gone.
        EmbeddingOrphanSweep.SweepableKinds.Should().Equal(
            EmbeddingOwnerKind.SoftSignal,
            EmbeddingOwnerKind.Topic,
            EmbeddingOwnerKind.Suggestion,
            EmbeddingOwnerKind.JournalEntry);
    }

    [Fact]
    public void SweepableKinds_ShouldExcludeProducerlessAndUnknownKinds()
    {
        // Rule / MarketSnapshot have no producer yet -- sweeping them would delete every such row to zero rather than
        // reclaim orphans -- and Unknown is the refusable sentinel. Being an allow-list, a value outside the enum (a
        // bad cast, or a kind not yet added) is excluded too, so a future kind is never swept until it is deliberately
        // added here alongside its producer check.
        IReadOnlyList<EmbeddingOwnerKind> sweepable = EmbeddingOrphanSweep.SweepableKinds;

        sweepable.Should().NotContain(EmbeddingOwnerKind.Unknown);
        sweepable.Should().NotContain(EmbeddingOwnerKind.Rule);
        sweepable.Should().NotContain(EmbeddingOwnerKind.MarketSnapshot);
        sweepable.Should().NotContain((EmbeddingOwnerKind)99);
    }

    [Fact]
    public void SweepableKinds_ShouldCoverEveryRetrievableKind_SoARetrievedKindIsNeverLeftUnswept()
    {
        // Every kind a consumer can retrieve is a kind whose owner can be deleted, so leaving one off the allow-list
        // would let its orphaned vectors accumulate forever and keep surfacing in recall until they were hydrated away.
        // Written as a mapping check, so adding a RetrievalKind without its producer check fails HERE rather than
        // silently shipping an un-GC'd kind.
        foreach (RetrievalKind kind in RetrievalKinds.All)
        {
            EmbeddingOrphanSweep.SweepableKinds.Should().Contain(EmbeddingOwnerKinds.For(kind));
        }
    }
}
