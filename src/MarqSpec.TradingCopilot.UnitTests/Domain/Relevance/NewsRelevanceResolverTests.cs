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
}
