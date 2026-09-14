using MarqSpec.TradingCopilot.Api.Ai;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Ai;

/// <summary>
/// The single mapping from the domain's <see cref="RetrievalKind"/> to the store's persisted
/// <see cref="EmbeddingOwnerKind"/> (gh#1065). It exists in exactly one place on purpose: the store's owner kinds are
/// part of a persisted primary key and can never move, while the retrieval kinds are a consumer-facing selector, so a
/// second copy of this mapping is a silent corruption waiting to happen.
/// </summary>
public class EmbeddingOwnerKindsTests
{
    [Theory]
    [InlineData(RetrievalKind.News, EmbeddingOwnerKind.SoftSignal)]
    [InlineData(RetrievalKind.Suggestion, EmbeddingOwnerKind.Suggestion)]
    [InlineData(RetrievalKind.JournalEntry, EmbeddingOwnerKind.JournalEntry)]
    public void For_ShouldMapEachRetrievalKind_ToItsPersistedOwnerKind(
        RetrievalKind kind, EmbeddingOwnerKind expected) =>
        EmbeddingOwnerKinds.For(kind).Should().Be(expected);

    [Fact]
    public void For_ShouldRefuse_TheUnknownKind()
    {
        // The refusable zero is never persisted, so mapping it is a caller error rather than a degrade -- exactly the
        // posture EmbeddingOrphanStore takes for a kind with no producer.
        Action map = () => EmbeddingOwnerKinds.For(RetrievalKind.Unknown);

        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void For_ShouldRefuse_AValueOutsideTheEnum()
    {
        Action map = () => EmbeddingOwnerKinds.For((RetrievalKind)99);

        map.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void For_ShouldMapEveryRetrievableKind_SoAddingOneCannotBeForgottenHere()
    {
        // RetrievalKinds.All is what a consumer may ask for wholesale; every member of it must map, or the first
        // "ground on everything" turn after a new kind lands throws instead of retrieving.
        foreach (RetrievalKind kind in RetrievalKinds.All)
        {
            EmbeddingOwnerKinds.For(kind).Should().NotBe(EmbeddingOwnerKind.Unknown);
        }
    }

    [Fact]
    public void All_ShouldListEveryRetrievableKind_AndNeverTheRefusableZero()
    {
        RetrievalKinds.All.Should().Equal(RetrievalKind.News, RetrievalKind.Suggestion, RetrievalKind.JournalEntry);
        RetrievalKinds.All.Should().NotContain(RetrievalKind.Unknown);
    }
}
