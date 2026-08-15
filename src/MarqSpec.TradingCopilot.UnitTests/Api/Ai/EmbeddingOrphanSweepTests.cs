using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

public class EmbeddingOrphanSweepTests
{
    [Fact]
    public void IsSweepable_ShouldBeTrue_ForProducerBackedKinds()
    {
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.SoftSignal).Should().BeTrue();
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.Topic).Should().BeTrue();
    }

    [Fact]
    public void IsSweepable_ShouldBeFalse_ForProducerlessAndUnknownKinds()
    {
        // Suggestion / Rule / MarketSnapshot have no producer yet -- sweeping them would delete every such row to
        // zero rather than reclaim orphans -- and Unknown is the refusable sentinel, never a real owner.
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.Unknown).Should().BeFalse();
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.Suggestion).Should().BeFalse();
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.Rule).Should().BeFalse();
        EmbeddingOrphanSweep.IsSweepable(EmbeddingOwnerKind.MarketSnapshot).Should().BeFalse();
    }

    [Fact]
    public void IsSweepable_ShouldBeFalse_ForAnUndefinedKind()
    {
        // Fail-safe: an allow-list, so a value outside the enum -- a bad cast, or a kind not yet added here -- is
        // never swept.
        EmbeddingOrphanSweep.IsSweepable((EmbeddingOwnerKind)99).Should().BeFalse();
    }

    [Fact]
    public void SweepableKinds_ShouldBeExactlyTheProducerBackedKinds()
    {
        EmbeddingOrphanSweep.SweepableKinds.Should().Equal(EmbeddingOwnerKind.SoftSignal, EmbeddingOwnerKind.Topic);
    }
}
