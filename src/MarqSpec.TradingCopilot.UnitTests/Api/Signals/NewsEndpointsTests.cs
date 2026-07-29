using MarqSpec.TradingCopilot.Api.Signals;
using MarqSpec.TradingCopilot.Data;
using MarqSpec.TradingCopilot.Data.Entities;
using MarqSpec.TradingCopilot.Data.Tenancy;
using MarqSpec.TradingCopilot.Domain.Signals;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Signals;

/// <summary>
/// The <c>/api/news</c> handlers (gh#27, ADR-0014): star/mute feedback and the personalized feed. The properties
/// that matter: feedback is a validated <b>upsert</b> (one per operator per item) that round-trips on clear; a star
/// reorders the feed toward similar items while an unstarred item <b>stays visible</b> (no filter bubble); the
/// caller's own feedback + a "why weighted" reason are surfaced; and every read/write is R-20-scoped (one operator's
/// stars never touch another's feed).
/// </summary>
public class NewsEndpointsTests
{
    private readonly Guid _operator = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _database = Guid.NewGuid().ToString();
    private static readonly IOptions<SalienceOptions> _options = Options.Create(new SalienceOptions());
    private static readonly DateTimeOffset _t = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    private sealed record FixedUser(Guid UserId) : ICurrentUser;

    private TradingCopilotDbContext Context(Guid? asUser = null) =>
        new(new DbContextOptionsBuilder<TradingCopilotDbContext>().UseInMemoryDatabase(_database).Options,
            new FixedUser(asUser ?? _operator));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static NewsFeedResponse FeedOf(IResult result) => (NewsFeedResponse)((IValueHttpResult)result).Value!;

    private async Task SeedNewsAsync(
        string dedupKey,
        DateTimeOffset publishedAt,
        IReadOnlyList<string>? instruments = null,
        IReadOnlyList<string>? topics = null,
        IReadOnlyList<string>? sources = null)
    {
        await using TradingCopilotDbContext context = Context();
        context.News.Add(new NewsRecord
        {
            DedupKey = dedupKey,
            Type = "news",
            Url = dedupKey,
            Title = dedupKey,
            Summary = string.Empty,
            PublishedAt = publishedAt,
            Tickers = [],
            SourceFeeds = [.. sources ?? []],
            RecordedAt = publishedAt,
            MatchedInstruments = [.. instruments ?? []],
            MatchedTopics = [.. topics ?? []],
        });
        await context.SaveChangesAsync();
    }

    private async Task<IResult> RateAsync(string dedupKey, SoftSignalKind kind = SoftSignalKind.Star, Guid? asUser = null)
    {
        await using TradingCopilotDbContext context = Context(asUser);
        return await NewsEndpoints.SetFeedbackAsync(
            new NewsFeedbackRequest(dedupKey, kind), new FixedUser(asUser ?? _operator), context, default);
    }

    // ---- feedback write ----

    [Fact]
    public async Task Star_CreatesFeedbackRow_OwnedByTheCaller()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);

        IResult result = await RateAsync("item");

        StatusOf(result).Should().Be(StatusCodes.Status204NoContent);
        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync();
        row.UserId.Should().Be(_operator);
        row.Kind.Should().Be(SoftSignalKind.Star);
        row.NewsDedupKey.Should().Be("item");
    }

    [Fact]
    public async Task ReRating_Replaces_RatherThanStacks()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", SoftSignalKind.Star);

        await RateAsync("item", SoftSignalKind.Mute);

        await using TradingCopilotDbContext verify = Context();
        SoftSignalFeedback row = await verify.SoftSignalFeedbacks.SingleAsync(); // still exactly one
        row.Kind.Should().Be(SoftSignalKind.Mute);
    }

    [Fact]
    public async Task Rating_APhantomKey_Is404()
    {
        IResult result = await RateAsync("does-not-exist");
        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(SoftSignalKind.Unknown)]
    [InlineData((SoftSignalKind)99)]
    public async Task Rating_WithABadKind_Is400(SoftSignalKind kind)
    {
        await SeedNewsAsync("item", _t);
        IResult result = await RateAsync("item", kind);
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Rating_WithNoKey_Is400()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await NewsEndpoints.SetFeedbackAsync(
            new NewsFeedbackRequest("   ", SoftSignalKind.Star), new FixedUser(_operator), context, default);
        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Clear_RemovesTheFeedback_AndRoundTripsToBase()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item");

        await using (TradingCopilotDbContext context = Context())
        {
            IResult cleared = await NewsEndpoints.ClearFeedbackAsync("item", context, default);
            StatusOf(cleared).Should().Be(StatusCodes.Status204NoContent);
        }

        await using TradingCopilotDbContext verify = Context();
        (await verify.SoftSignalFeedbacks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_WhenAbsent_Is404()
    {
        await using TradingCopilotDbContext context = Context();
        IResult result = await NewsEndpoints.ClearFeedbackAsync("nope", context, default);
        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ---- feed read ----

    [Fact]
    public async Task Feed_ColdStart_OrdersByRecency()
    {
        await SeedNewsAsync("older", _t, instruments: ["ES"]);
        await SeedNewsAsync("newer", _t.AddMinutes(5), instruments: ["NQ"]);

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, default));

        feed.Items.Select(item => item.DedupKey).Should().Equal("newer", "older"); // equal base -> recency
    }

    [Fact]
    public async Task Feed_RanksAStarredSimilarItem_AboveAMoreRecentUnrelatedOne()
    {
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await SeedNewsAsync("nq-newer", _t.AddMinutes(5), instruments: ["NQ"]); // more recent
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await RateAsync("es-seed"); // star a prior ES story

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, default));

        feed.Items.First().DedupKey.Should().Be("es-fresh"); // starred-similar beats mere recency
        feed.Items.Should().Contain(item => item.DedupKey == "nq-newer"); // unrelated item still visible (no bubble)
    }

    [Fact]
    public async Task Feed_SurfacesTheCallersOwnFeedback()
    {
        await SeedNewsAsync("item", _t, instruments: ["ES"]);
        await RateAsync("item");

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, default));

        feed.Items.Single(item => item.DedupKey == "item").Feedback.Should().Be(SoftSignalKind.Star);
    }

    [Fact]
    public async Task Feed_ExplainsWhyAnItemWasWeighted()
    {
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await RateAsync("es-seed");

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, default));

        NewsFeedItemResponse boosted = feed.Items.Single(item => item.DedupKey == "es-fresh");
        boosted.Multiplier.Should().BeGreaterThan(1.0);
        boosted.WhyWeighted.Should().Contain("ES");
        boosted.Reasons.Should().Contain(reason => reason.Value == "ES");
    }

    [Fact]
    public async Task Feed_RespectsTheLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            await SeedNewsAsync($"item-{i}", _t.AddMinutes(i));
        }

        await using TradingCopilotDbContext read = Context();
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(2, read, _options, default));

        feed.Items.Should().HaveCount(2);
    }

    // ---- R-20 isolation ----

    [Fact]
    public async Task OneOperatorsStars_DoNotTouchAnothersFeed()
    {
        await SeedNewsAsync("es-fresh", _t, instruments: ["ES"]);
        await SeedNewsAsync("es-seed", _t.AddDays(-1), instruments: ["ES"]);
        await RateAsync("es-seed", asUser: _operator); // operator stars ES

        // The OTHER operator has no feedback, so their ES item sits at base (multiplier 1) with no own-feedback.
        await using TradingCopilotDbContext read = Context(_other);
        NewsFeedResponse feed = FeedOf(await NewsEndpoints.GetFeedAsync(50, read, _options, default));

        NewsFeedItemResponse fresh = feed.Items.Single(item => item.DedupKey == "es-fresh");
        fresh.Multiplier.Should().Be(1.0);
        fresh.Feedback.Should().BeNull();
    }

    [Fact]
    public async Task AnOperator_CannotClearAnothersFeedback()
    {
        await SeedNewsAsync("item", _t);
        await RateAsync("item", asUser: _operator);

        await using TradingCopilotDbContext asOther = Context(_other);
        IResult result = await NewsEndpoints.ClearFeedbackAsync("item", asOther, default);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound); // invisible to the other operator (R-20)
    }
}
