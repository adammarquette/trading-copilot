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
    public void TitleSimilarity_ShouldNotCountSingleCharacterAbbreviationFragments_AsSharedWords() =>
        // "U.S." / "U.K." tokenize to single-character u/s/k, which are dropped -- otherwise every American headline
        // shares junk tokens with every other (#827 review). Two unrelated headlines share nothing here.
        NewsFuzzyDedup.TitleSimilarity("U.S. oil rises", "U.K. gas falls")
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

    [Fact]
    public void AreLikelyTheSameStory_ShouldMergeAFullerReHeadlineOfTheSameStory() =>
        // The value case the fallback exists for: one provider's headline is a fuller version of the other's -- its
        // content a superset, nothing divergent -- so it merges even though the URLs (and lengths) differ.
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Apple unveils the new iPhone 17 today", _published, ["AAPL"],
            "Apple unveils iPhone 17", _published.AddMinutes(2), ["AAPL"])
            .Should().BeTrue("a fuller re-headline of the same story has no content the shorter one lacks");

    // ---- #827 review: template-collision negatives, close to the boundary, same ticker, minutes apart ----

    [Fact]
    public void AreLikelyTheSameStory_ShouldNotMergeTwoDistinctMacroReleases_SharingOnlyTheirBoilerplate() =>
        // The reviewer's boundary negative. Unweighted word overlap scored this 0.75 and merged it; two distinct
        // macro releases share their boilerplate ("fall ... expected"), not their story ("jobless claims" vs "retail
        // sales") -- each asserts content the other lacks, so the subset rule refuses.
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "U.S. jobless claims fall more than expected", _published, ["ES"],
            "U.S. retail sales fall more than expected", _published.AddMinutes(4), ["ES"])
            .Should().BeFalse("two distinct macro releases share their boilerplate, not their story");

    [Fact]
    public void AreLikelyTheSameStory_ShouldNotMergeTwoCentralBanks_OnAnOtherwiseIdenticalHeadline() =>
        // Bag-of-words scored this ~0.89 -- ABOVE a genuine near-duplicate -- because only one content word differs.
        // The subset rule refuses: "Fed" and "ECB" are each content the other lacks, so this is two stories.
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Fed holds rates steady, signals one cut this year", _published, ["ES"],
            "ECB holds rates steady, signals one cut this year", _published.AddMinutes(5), ["ES"])
            .Should().BeFalse("two different central banks are two stories, however template-identical the headlines");

    [Fact]
    public void AreLikelyTheSameStory_ShouldNotMergeTwoSpeakers_OnAnOtherwiseIdenticalHeadline() =>
        NewsFuzzyDedup.AreLikelyTheSameStory(
            "Powell says inflation remains too high", _published, ["ES"],
            "Williams says inflation remains too high", _published.AddMinutes(2), ["ES"])
            .Should().BeFalse("two different speakers are two stories, not one");
}
