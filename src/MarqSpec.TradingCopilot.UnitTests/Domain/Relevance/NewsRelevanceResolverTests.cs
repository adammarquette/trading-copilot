using MarqSpec.TradingCopilot.Domain.Relevance;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Relevance;

/// <summary>
/// The pure relevance resolver (gh#359). The safety-relevant properties: no cross-contamination (a ticker maps
/// only to its configured instruments), an unmapped ticker falls through with no misattribution, and the output
/// is deterministic (sorted) so the same story against the same config always resolves the same way.
/// </summary>
public class NewsRelevanceResolverTests
{
    private static readonly IReadOnlyList<TickerInstrumentMapping> _maps =
    [
        new("SPY", "ES"),
        new("SPY", "MES"), // a ticker maps to several
        new("QQQ", "NQ"),
    ];

    private static readonly IReadOnlyList<NewsTopicDefinition> _topics =
    [
        new("fomc", ["FOMC", "rate decision"], TopicScope.Global, null),
        new("crude", ["crude", "oil"], TopicScope.Instrument, "CL"),
    ];

    [Fact]
    public void Resolve_ShouldMapTickersToInstruments_Composing()
    {
        NewsRelevanceResolver.Resolve(["SPY"], "", _maps, _topics)
            .Instruments.Should().BeEquivalentTo(["ES", "MES"]);
    }

    [Fact]
    public void Resolve_ShouldBeCaseInsensitiveOnTickers()
    {
        NewsRelevanceResolver.Resolve(["spy"], "", _maps, _topics).Instruments.Should().Contain("ES");
    }

    [Fact]
    public void Resolve_ShouldFallThroughCleanly_ForAnUnmappedTicker()
    {
        // No misattribution: an unmapped ticker yields no instrument (topics may still match).
        NewsRelevanceResolver.Resolve(["ZZZZ"], "", _maps, _topics).Instruments.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ShouldNotCrossContaminate()
    {
        // SPY news maps to ES / MES only — never NQ (QQQ's instrument).
        NewsRelevanceResolver.Resolve(["SPY"], "", _maps, _topics).Instruments.Should().NotContain("NQ");
    }

    [Fact]
    public void Resolve_ShouldMatchGlobalTopics_ByKeyword_WithNoInstrument()
    {
        RelevanceMatch match = NewsRelevanceResolver.Resolve([], "The FOMC held rates steady.", _maps, _topics);

        match.Topics.Should().Contain("fomc");
        match.Instruments.Should().BeEmpty(); // a global topic attaches no instrument
    }

    [Fact]
    public void Resolve_ShouldMatchInstrumentScopedTopics_AndMarkTheInstrument()
    {
        RelevanceMatch match = NewsRelevanceResolver.Resolve([], "Crude oil surged today.", _maps, _topics);

        match.Topics.Should().Contain("crude");
        match.Instruments.Should().Contain("CL"); // an instrument-scoped topic marks its instrument
    }

    [Fact]
    public void Resolve_ShouldBeDeterministic_SortedRegardlessOfInputOrder()
    {
        RelevanceMatch first = NewsRelevanceResolver.Resolve(["QQQ", "SPY"], "FOMC", _maps, _topics);
        RelevanceMatch second = NewsRelevanceResolver.Resolve(["SPY", "QQQ"], "fomc", _maps, _topics);

        first.Instruments.Should().Equal(second.Instruments);
        first.Instruments.Should().Equal("ES", "MES", "NQ"); // sorted, deduped
    }

    [Fact]
    public void Resolve_ShouldReturnEmpty_WhenNothingMatches()
    {
        RelevanceMatch match = NewsRelevanceResolver.Resolve(["ZZZ"], "nothing relevant here", _maps, _topics);

        match.Instruments.Should().BeEmpty();
        match.Topics.Should().BeEmpty();
    }

    // --- Semantic topic match (gh#854): a news vector near a topic's stored embedding matches the topic,
    // --- additively to keyword matching. The vectors below are controlled so the cosine is exact -- 1 for
    // --- identical, 0 for orthogonal or dimension-mismatched -- the same controlled-vector style as
    // --- EmbeddingSimilarityTests, so a threshold change never silently flips these.

    [Fact]
    public void Resolve_ShouldMatchTopic_BySemanticSimilarity_WhenTheNewsVectorIsNearTheTopicVector_AndNoKeywordMatches()
    {
        // The topic's keywords do NOT appear in the text, so only the semantic path can match it: [1,0]·[1,0] = 1.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("monetary-policy", ["FOMC", "rate decision"], TopicScope.Global, null, Embedding: [1f, 0f]),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "The central bank signalled a hawkish tilt.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().Contain("monetary-policy");
    }

    [Fact]
    public void Resolve_ShouldStillMatchByKeyword_WhenNoNewsVectorIsSupplied()
    {
        // Additive: with no news vector the semantic path is off and matching is exactly the keyword behaviour
        // (the eight tests above all exercise this four-arg form unchanged).
        RelevanceMatch match = NewsRelevanceResolver.Resolve([], "The FOMC held rates steady.", _maps, _topics);

        match.Topics.Should().Contain("fomc");
    }

    [Fact]
    public void Resolve_ShouldMatchByKeywordOrSemantic_Additively()
    {
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("fomc", ["FOMC"], TopicScope.Global, null, Embedding: [0f, 1f]),                 // keyword hits; vector far
            new("sentiment", ["nonexistent-kw"], TopicScope.Global, null, Embedding: [1f, 0f]), // vector hits; keyword absent
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "The FOMC met today.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().BeEquivalentTo(["fomc", "sentiment"]);
    }

    [Fact]
    public void Resolve_ShouldAttachInstrument_ForASemanticallyMatchedInstrumentScopedTopic()
    {
        // A semantic hit on an instrument-scoped topic attaches its instrument, exactly as a keyword hit does.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("crude", ["crude", "oil"], TopicScope.Instrument, "CL", Embedding: [1f, 0f]),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "Energy markets rallied on supply fears.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().Contain("crude");
        match.Instruments.Should().Contain("CL");
    }

    [Fact]
    public void Resolve_ShouldNotSemanticMatch_WhenTheTopicHasNoEmbedding()
    {
        // A topic without a stored embedding must never match everything -- it falls back to keyword-only.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("unembedded", ["nonexistent-kw"], TopicScope.Global, null),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "Unrelated story text.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ShouldNotSemanticMatch_WhenBelowTheThreshold()
    {
        // Orthogonal vectors have cosine 0, below the default threshold -- no match.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("far", ["nonexistent-kw"], TopicScope.Global, null, Embedding: [0f, 1f]),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "Unrelated story text.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ShouldNotSemanticMatch_WhenTheThresholdIsNonPositive()
    {
        // The fail-safe must not invert: a threshold of 0 (a future mis-configuration) must NOT match everything,
        // even for a perfect cosine of 1. A non-positive threshold disables the semantic path outright.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("perfect", ["nonexistent-kw"], TopicScope.Global, null, Embedding: [1f, 0f]),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "Unrelated story text.", _maps, topics, newsVector: [1f, 0f], semanticThreshold: 0.0);

        match.Topics.Should().BeEmpty("a non-positive threshold disables the semantic path — never match-everything");
    }

    [Fact]
    public void Resolve_ShouldNotThrowAndNotMatch_OnADimensionMismatchBetweenVectors()
    {
        // Vectors of different widths (different models) score 0, never throw -- the topic does not match.
        IReadOnlyList<NewsTopicDefinition> topics =
        [
            new("mismatch", ["nonexistent-kw"], TopicScope.Global, null, Embedding: [1f, 0f, 0f]),
        ];

        RelevanceMatch match = NewsRelevanceResolver.Resolve(
            [], "Unrelated story text.", _maps, topics, newsVector: [1f, 0f]);

        match.Topics.Should().BeEmpty();
    }
}
