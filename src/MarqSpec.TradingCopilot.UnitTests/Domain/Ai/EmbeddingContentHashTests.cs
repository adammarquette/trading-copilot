using System.Text.RegularExpressions;
using MarqSpec.TradingCopilot.Domain.Ai;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Ai;

/// <summary>
/// The embedding idempotence guard (gh#377): a pure function of the exact text handed to the provider, so the
/// news-embedding pass can decide "unchanged, skip the paid call" vs. "content changed, re-embed" without a
/// database or a provider round-trip.
/// </summary>
public class EmbeddingContentHashTests
{
    [Fact]
    public void For_ShouldReturnTheSameHash_ForTheSameContent()
    {
        string content = "FOMC holds rates\n\nThe committee left rates unchanged.";

        EmbeddingContentHash.For(content).Should().Be(EmbeddingContentHash.For(content));
    }

    [Fact]
    public void For_ShouldReturnADifferentHash_WhenTheContentChanges()
    {
        // THE load-bearing property: a title/summary revision must be recognised as "needs re-embedding" rather
        // than silently skipped as unchanged.
        string original = "FOMC holds rates\n\nThe committee left rates unchanged.";
        string revised = "FOMC holds rates\n\nThe committee left rates unchanged, citing inflation risk.";

        EmbeddingContentHash.For(original).Should().NotBe(EmbeddingContentHash.For(revised));
    }

    [Fact]
    public void For_ShouldBeSensitiveToOrdering_SoTitleAndSummarySwapDoNotCollide()
    {
        EmbeddingContentHash.For("A\n\nB").Should().NotBe(EmbeddingContentHash.For("B\n\nA"));
    }

    [Fact]
    public void For_ShouldReturnASixtyFourCharacterLowercaseHexDigest_FittingTheContentHashColumn()
    {
        // ContentHash is character varying(64) -- a SHA-256 hex digest fits it EXACTLY, with no truncation.
        string hash = EmbeddingContentHash.For("Fed holds rates");

        hash.Should().HaveLength(64);
        Regex.IsMatch(hash, "^[0-9a-f]{64}$").Should().BeTrue("the hash must be lower-case hex, matching ContentHash's stored form");
    }

    [Fact]
    public void For_ShouldNotThrow_WhenContentIsEmpty()
    {
        // Unlike NewsDedupKey's URL (an identity), embedded content has no "must be non-blank" requirement of its
        // own -- an empty title/summary hashes to a well-defined, stable value like any other string.
        Action act = () => EmbeddingContentHash.For(string.Empty);

        act.Should().NotThrow();
    }

    [Fact]
    public void For_ShouldThrow_WhenContentIsNull()
    {
        Action act = () => EmbeddingContentHash.For(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
