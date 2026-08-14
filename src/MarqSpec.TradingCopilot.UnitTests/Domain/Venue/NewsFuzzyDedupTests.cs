using MarqSpec.TradingCopilot.Domain.Venue;

namespace MarqSpec.TradingCopilot.UnitTests.Domain.Venue;

/// <summary>
/// The R-2 fuzzy news-dedup fallback (gh#764): two items are the same story only when their titles are similar AND
/// they published within the window AND they share a ticker. The false-<b>positive</b> direction is the one that
/// matters — a wrongly-merged row destroys a real signal — so the conjunction and its thresholds are pinned here.
/// </summary>
public class NewsFuzzyDedupTests
{
    private static readonly DateTimeOffset _published = new(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);

    // ---- TitleSimilarity: the Sørensen–Dice measure ----

    [Fact]
    public void TitleSimilarity_ShouldBeOne_ForIdenticalTitlesUpToCaseAndPunctuation() =>
        NewsFuzzyDedup.TitleSimilarity("Fed holds rates steady", "fed, holds! RATES  steady.")
            .Should().Be(1d);

    [Fact]
    public void TitleSimilarity_ShouldBeZero_WhenNoWordsOverlap() =>
        NewsFuzzyDedup.TitleSimilarity("Apple unveils iPhone", "Oil futures slide")
            .Should().Be(0d);

    [Fact]
    public void TitleSimilarity_ShouldBeZero_WhenEitherTitleHasNoWords() =>
        NewsFuzzyDedup.TitleSimilarity("", "Fed holds rates")
            .Should().Be(0d);

    [Fact]
    public void TitleSimilarity_ShouldRankASyndicatedNearDuplicateAboveTheThreshold_AndADistinctStoryBelowIt()
    {
        // The property the whole card rests on: a syndicated near-duplicate clears MinTitleSimilarity while two
        // distinct same-ticker stories do not -- so the number sits in the gap between them, with margin.
        double nearDuplicate = NewsFuzzyDedup.TitleSimilarity(
            "Apple unveils the iPhone 17 at its fall event",
            "Apple unveils iPhone 17");
        double distinct = NewsFuzzyDedup.TitleSimilarity(
            "Apple unveils iPhone 17",
            "Apple stock slides after earnings miss");

        nearDuplicate.Should().BeGreaterThanOrEqualTo(NewsFuzzyDedup.MinTitleSimilarity);
        distinct.Should().BeLessThan(NewsFuzzyDedup.MinTitleSimilarity);
    }

    // ---- AreLikelyTheSameStory: the three-signal conjunction ----

    [Fact]
    public void AreLikelyTheSameStory_ShouldBeTrue_WhenTitleTimeAndTickerAllAgree() =>
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils the iPhone 17 at its fall event", _published, ["AAPL"],
            "Apple unveils iPhone 17", _published.AddMinutes(3), ["AAPL", "SPY"])
            .Should().BeTrue();

    [Fact]
    public void AreLikelyTheSameStory_ShouldBeFalse_ForTwoDistinctStoriesOnTheSameTickerCloseInTime() =>
        // The false-positive the card exists to avoid: same ticker, minutes apart, but genuinely different stories.
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils iPhone 17", _published, ["AAPL"],
            "Apple stock slides after earnings miss", _published.AddMinutes(5), ["AAPL"])
            .Should().BeFalse("two distinct same-ticker stories must never collapse -- a merge destroys a real signal");

    [Fact]
    public void AreLikelyTheSameStory_ShouldBeFalse_WhenPublishedOutsideTheWindow() =>
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils the iPhone 17 at its fall event", _published, ["AAPL"],
            "Apple unveils iPhone 17", _published.Add(NewsFuzzyDedup.MaxPublishedGap).AddMinutes(1), ["AAPL"])
            .Should().BeFalse("beyond the publication-time window they are not treated as the same story");

    [Fact]
    public void AreLikelyTheSameStory_ShouldBeFalse_WhenNoTickerIsShared_EvenIfTitlesMatch() =>
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils iPhone 17", _published, ["AAPL"],
            "Apple unveils iPhone 17", _published.AddMinutes(1), ["MSFT"])
            .Should().BeFalse("ticker overlap is a required R-2 signal -- title + time alone never merge");

    [Fact]
    public void AreLikelyTheSameStory_ShouldBeFalse_WhenEitherItemCarriesNoTicker() =>
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils iPhone 17", _published, [],
            "Apple unveils iPhone 17", _published.AddMinutes(1), ["AAPL"])
            .Should().BeFalse("with no ticker to overlap on, the fallback does not fire");
}
