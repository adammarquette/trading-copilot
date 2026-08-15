using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

public class EmbeddingOrphanSweepTests
{
    [Fact]
    public void SweepableKinds_ShouldBeExactlyTheProducerBackedKinds()
    {
        // The sweep iterates exactly this list, so it IS the allow-list: SoftSignal (checked against News) and Topic
        // (against NewsTopics) -- the only kinds with a producer to prove an owner gone.
        EmbeddingOrphanSweep.SweepableKinds.Should().Equal(EmbeddingOwnerKind.SoftSignal, EmbeddingOwnerKind.Topic);
    }

    [Fact]
    public void SweepableKinds_ShouldExcludeProducerlessAndUnknownKinds()
    {
        // Suggestion / Rule / MarketSnapshot have no producer yet -- sweeping them would delete every such row to
        // zero rather than reclaim orphans -- and Unknown is the refusable sentinel. Being an allow-list, a value
        // outside the enum (a bad cast, or a kind not yet added) is excluded too, so a future kind is never swept
        // until it is deliberately added here alongside its producer check.
        IReadOnlyList<EmbeddingOwnerKind> sweepable = EmbeddingOrphanSweep.SweepableKinds;

        sweepable.Should().NotContain(EmbeddingOwnerKind.Unknown);
        sweepable.Should().NotContain(EmbeddingOwnerKind.Suggestion);
        sweepable.Should().NotContain(EmbeddingOwnerKind.Rule);
        sweepable.Should().NotContain(EmbeddingOwnerKind.MarketSnapshot);
        sweepable.Should().NotContain((EmbeddingOwnerKind)99);
    }
}
