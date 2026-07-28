using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// The cross-source dedup key (gh#358, R-2) — the canonical URL identity that collapses the same story from two
/// providers into one row and makes an overlapping re-poll idempotent. The quiet failure it guards is a duplicate:
/// the same article stored twice would double-count in every downstream salience and retrieval.
/// </summary>
public class NewsDedupKeyTests
{
    [Fact]
    public void For_ShouldStripTrackingParameters()
    {
        // Two providers link the same article; one decorates the URL with campaign tracking. Same story.
        NewsDedupKey.For("https://site.com/story?utm_source=news&utm_campaign=x&id=7")
            .Should().Be(NewsDedupKey.For("https://site.com/story?id=7"));
    }

    [Fact]
    public void For_ShouldCollapseScheme_Www_AndTrailingSlash()
    {
        NewsDedupKey.For("https://www.site.com/story/")
            .Should().Be(NewsDedupKey.For("http://site.com/story"));
    }

    [Fact]
    public void For_ShouldLowercaseTheHostButKeepThePathCase()
    {
        // Hosts are case-insensitive; paths are not. Lower-casing the whole thing would merge distinct articles.
        NewsDedupKey.For("https://SITE.com/Story-About-The-Fed").Should().Be("site.com/Story-About-The-Fed");
    }

    [Fact]
    public void For_ShouldSortRemainingQueryParameters()
    {
        NewsDedupKey.For("https://site.com/s?b=2&a=1").Should().Be(NewsDedupKey.For("https://site.com/s?a=1&b=2"));
    }

    [Fact]
    public void For_ShouldKeepDistinctStoriesDistinct()
    {
        NewsDedupKey.For("https://site.com/a").Should().NotBe(NewsDedupKey.For("https://site.com/b"));
    }

    [Fact]
    public void For_ShouldFallBackToTheLowercasedRaw_WhenNotAnAbsoluteUrl()
    {
        // A malformed feed still needs a stable key so it can at least dedup against itself.
        NewsDedupKey.For("Not-A-URL").Should().Be("not-a-url");
    }

    [Fact]
    public void For_ShouldThrow_WhenTheUrlIsBlank()
    {
        Action act = () => NewsDedupKey.For("   ");

        act.Should().Throw<ArgumentException>();
    }
}
